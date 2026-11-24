#!/usr/bin/env python3
"""Provision the OpenS4L stack: create the admin account and load the free shop (PostgreSQL).

Runs as a one-shot Docker container. Waits for the Game server to have applied its EF Core
migrations (which create the DB tables), then:
  1. creates the admin account (SecurityLevel 3) if it doesn't exist,
  2. regenerates the "unlock everything" shop with correct client tabs and loads it.

Idempotent: safe to re-run on an already-provisioned stack.

Env:
  DB_HOST           PostgreSQL hostname on the compose network (default: postgres)
  DB_ROOT_PASSWORD  postgres superuser password (default: 1234)
  ADMIN_USER        admin username, stored lowercase (default: admin)
  ADMIN_PASSWORD    admin password (default: admin)
  ITEM_X7           path to the server's item.x7 (default: /data/xml/item.x7)
  WEAPON_X7         path to _eu_weapon.x7 (default: /data/xml/_eu_weapon.x7)
  CAPSULE_X7        path to _eu_item_tooltip_addcapsule.x7, used to file capsules under
                    the right Capsule & Set sub tab (optional; default under /data/xml)
  BOT_COUNT         number of regular bot accounts to create (default: 0 = none)
  BOT_PREFIX        bot account username prefix (default: bot) -> bot0..botN-1
  BOT_PASSWORD      shared bot password (default: admin)
  ONLY_BOTS         when truthy, only create bot accounts (skip admin/shop/channels)

Shop shape (all optional; without them the shop is the plain free one described above):
  ITEM_STRINGS      iteminfo_string_table.x7; items without an English name are hidden
                    (default: /data/language/xml/iteminfo_string_table.x7)
  EFFECT_LIST_X7    effect_list.x7 / EFFECT_MATCH_X7 effect_match_list.x7: effects the client
                    knows (defaults under /data/xml)
  CLIENT_ICONS      text file with the client's icon file names (one per line), or
  RESOURCES_DIR     the client's resources/ folder; with either, items whose icon the client
                    lacks are hidden (defaults: /data/server/client_icons.txt, /data/resources)
  SHOP_REF          operator-supplied reference shop: a JSON made by `provision.py
                    extract-ref`, or one or more SQL dumps of an official-era shop
                    (comma separated, first wins per item). Supplies colours, stat effects and
                    the period options per item. Default: /data/server/shop_ref.json if present.
                    Never commit such a file: it is the original game's data.
  SHOP_DAY_PERIODS  1 (default) = offer the reference's day periods (1/3/7/15/30 days) next to
                    permanent; 0 = permanent only
  SHOP_SET_PERIODS  best (default) = list one period variant per costume set (permanent, else
                    the longest); all = list every "(1 day)/(7 days)/..." set item
  SHOP_REF_ONLY     1 = only list items the reference shop lists (default 0)

CLI (no DB needed):
  provision.py extract-ref DUMP.sql [DUMP2.sql ...] -o shop_ref.json [--client-dir GAME_DIR]
      turns SQL dumps into the JSON SHOP_REF reads; with --client-dir also writes
      client_icons.txt next to it (icon names under GAME_DIR/resources/image).
"""
import base64
import hashlib
import json
import os
import re
import sys
import time
import xml.etree.ElementTree as ET

try:
    import psycopg2
except ImportError:  # the extract-ref CLI runs without a DB driver
    psycopg2 = None

DB_HOST = os.getenv("DB_HOST", "postgres")
ROOT_PW = os.getenv("DB_ROOT_PASSWORD", "1234")
ADMIN_USER = os.getenv("ADMIN_USER", "admin").lower()
ADMIN_PASS = os.getenv("ADMIN_PASSWORD", "admin")
ITEM_X7 = os.getenv("ITEM_X7", "/data/xml/item.x7")
WEAPON_X7 = os.getenv("WEAPON_X7", "/data/xml/_eu_weapon.x7")
CAPSULE_X7 = os.getenv("CAPSULE_X7", "/data/xml/_eu_item_tooltip_addcapsule.x7")
SCHEMA_TIMEOUT = int(os.getenv("SCHEMA_TIMEOUT", "180"))
BOT_COUNT = int(os.getenv("BOT_COUNT", "0"))
BOT_PREFIX = os.getenv("BOT_PREFIX", "bot")
BOT_PASSWORD = os.getenv("BOT_PASSWORD", "admin")
ONLY_BOTS = os.getenv("ONLY_BOTS", "0").lower() in ("1", "true", "yes")
ITEM_STRINGS = os.getenv("ITEM_STRINGS", "/data/language/xml/iteminfo_string_table.x7")
EFFECT_LIST_X7 = os.getenv("EFFECT_LIST_X7", "/data/xml/effect_list.x7")
EFFECT_MATCH_X7 = os.getenv("EFFECT_MATCH_X7", "/data/xml/effect_match_list.x7")
CLIENT_ICONS = os.getenv("CLIENT_ICONS", "/data/server/client_icons.txt")
RESOURCES_DIR = os.getenv("RESOURCES_DIR", "/data/resources")
SHOP_REF = os.getenv("SHOP_REF", "")
SHOP_REF_DEFAULT = "/data/server/shop_ref.json"
SHOP_DAY_PERIODS = os.getenv("SHOP_DAY_PERIODS", "1").lower() in ("1", "true", "yes")
SHOP_SET_PERIODS = os.getenv("SHOP_SET_PERIODS", "best").lower()
SHOP_REF_ONLY = os.getenv("SHOP_REF_ONLY", "0").lower() in ("1", "true", "yes")

# Matching OpenS4L.Common/Cryptography/PasswordHasher.cs
PBKDF2_ITERATIONS = 24000
PBKDF2_DKLEN = 24
SECURITY_LEVEL_ADMIN = 3


def connect(db=None):
    return psycopg2.connect(
        host=DB_HOST, port=5432, user="postgres", password=ROOT_PW,
        dbname=db, connect_timeout=5,
    )


def table_exists(cur, name):
    cur.execute(
        "SELECT 1 FROM information_schema.tables "
        "WHERE table_schema='public' AND table_name=%s", (name,))
    return cur.fetchone() is not None


def wait_for_schema():
    deadline = time.time() + SCHEMA_TIMEOUT
    while time.time() < deadline:
        try:
            with connect("game") as c, c.cursor() as cur:
                has_shop = table_exists(cur, "shop_items")
            with connect("auth") as c, c.cursor() as cur:
                has_accounts = table_exists(cur, "accounts")
            if has_shop and has_accounts:
                print("[provision] schema ready (shop_items + accounts exist)")
                return
        except Exception:
            pass
        print("[provision] waiting for DB schema (game server migrations)...")
        time.sleep(3)
    raise SystemExit(
        f"[provision] timed out after {SCHEMA_TIMEOUT}s waiting for schema. "
        "Is PostgreSQL up and has the game server started once?"
    )


def ensure_admin():
    with connect("auth") as conn, conn.cursor() as cur:
        cur.execute('SELECT COUNT(*) FROM "accounts" WHERE "Username"=%s', (ADMIN_USER,))
        if cur.fetchone()[0] > 0:
            print(f"[provision] admin account '{ADMIN_USER}' already exists (skipped)")
            return
        salt = os.urandom(PBKDF2_DKLEN)
        key = hashlib.pbkdf2_hmac("sha1", ADMIN_PASS.encode(), salt,
                                  PBKDF2_ITERATIONS, dklen=PBKDF2_DKLEN)
        cur.execute(
            'INSERT INTO "accounts" ("Username", "Nickname", "Password", "Salt", "SecurityLevel") '
            "VALUES (%s, NULL, %s, %s, %s)",
            (ADMIN_USER,
             base64.b64encode(key).decode(),
             base64.b64encode(salt).decode(),
             SECURITY_LEVEL_ADMIN),
        )
        print(f"[provision] created admin account '{ADMIN_USER}' (SecurityLevel 3)")


def ensure_channels():
    """Seed the game server's channel list (the client shows a Server, then the channels
    you can enter from the `channels` table in the game DB). Mirrors the EU v1267 client's
    `_eu_channel_setting.x7` layout (ids 1-11). The Game server's ChannelService loads this
    table at startup; with it empty the client sees a Server but no channel to enter (and so
    no room list / create-room screen). Color is an 8-digit ARGB hex string (varchar(8)),
    parsed by the server via Color.FromArgb(int.Parse(hex)).
    """
    # (id, name, description, rank, color_argb, player_limit, min_level, max_level)
    CHANNELS = [
        (1,  "Beginner 1", "ROOKIE",         "FREE", "FF297FFF", 400,  0,  20),
        (2,  "Beginner 2", "ROOKIE",         "FREE", "FF297FFF", 400,  0,  20),
        (3,  "Amateur 1",  "Super Rookie",   "FREE", "FF9B59FF", 600, 15,  35),
        (4,  "Amateur 2",  "Super Rookie",   "FREE", "FF9B59FF", 600, 15,  35),
        (5,  "Pro 1",      "Pro",            "FREE", "FF00A2FF", 600, 30,  55),
        (6,  "Pro 2",      "Pro",            "FREE", "FF00A2FF", 600, 30,  55),
        (7,  "Elite 1",    "Elite",          "FREE", "FFFF7F00", 600, 50,  80),
        (8,  "Elite 2",    "Elite",          "FREE", "FFFF7F00", 600, 50,  80),
        (9,  "Free 1",     "FREE",           "FREE", "FF00FF7F", 600,  0, 999),
        (10, "Free 2",     "Free Event",     "FREE", "FF00FF7F", 600,  8,  80),
        (11, "Clan",       "CLUB",           "FREE", "FF00FF7F", 300,  0, 999),
    ]
    with connect("game") as conn, conn.cursor() as cur:
        cur.execute('SELECT COUNT(*) FROM "channels"')
        count = cur.fetchone()[0]
        if count > 0:
            print(f"[provision] channels: {count} already present (skipped)")
            return
        for (cid, name, desc, rank, color, limit, mn, mx) in CHANNELS:
            cur.execute(
                'INSERT INTO "channels" ("Id", "Name", "Description", "Color", '
                '"PlayerLimit", "MinLevel", "MaxLevel") VALUES (%s,%s,%s,%s,%s,%s,%s)',
                (cid, name, desc, color, limit, mn, mx))
    print(f"[provision] channels: {len(CHANNELS)} seeded (Beginner..Free/Clan)")


# Client shop/inventory tabs (shop_ inven.xui; the inventory window uses the same tabs and
# filters on the same MainTab/SubTab the server sends). MainTab: 1 Capsule & Set,
# 2 Weapon & Skill, 3 Costumes, 4 Items. The SubTab values are the ones the official shop
# used, as preserved in the public Netsphere shop dumps (EU 1332 and Season 8). They agree
# except for 403xxxx capsules (S8 files them all under Total) and two rifles; we follow 1332.
#   Capsule & Set: 1 Costume, 2 Melee, 3 Rifle (any ranged weapon), 4 Etc, 5 Pets, 6 PEN, 7 Set
#   Weapon & Skill: 1 Melee, 2 Rifle, 3 Guns, 4 Heavy gun, 5 Snipe, 6 Special, 7 Throwing, 8 Skill
#   Costumes: 2 Head, 3 Face, 4 Jacket, 5 Pants, 6 Gloves, 7 Shoes, 8 Accessories, 9 Pets
#   Items: 0 (Total only), 2 Esper Chip, 3 Enchant, 4 Pen Bonus, 5 Plus, 6 Etc
CAPSULE_SUBS = (2, 3, 6, 11, 12, 16)   # OneTimeUse subcategories holding capsules/boxes
WEAPON_SUB_TAB = {0: 1, 2: 4, 3: 5, 4: 6, 5: 7, 6: 6}  # 1 (rifle/gun) is split by type
BOOST_SUB_TAB = {0: 4, 1: 5, 2: 5, 3: 3, 4: 5}          # PEN+ / EXP+ / MP+ / unique boosters
PEN_ICON = 4040003     # the PEN icon capsule tooltips show for a PEN reward
COUPON_ICON = 6000001  # the coupon icon most capsules list as a filler reward


def _capsule_sub(item_id, capsule_icons, item_icons, depth=0):
    """Capsule & Set sub tab, from the rewards the client's capsule tooltip shows.

    The first reward icon that isn't the PEN/coupon filler decides it; a capsule that only
    shows PEN is a PEN capsule. Matches the EU 1332 dump for ~96% of the capsules it has.
    """
    icons = capsule_icons.get(item_id)
    if not icons:
        return 6 if "pen" in item_icons.get(item_id, "") else 1
    main = [i for i in icons if i not in (PEN_ICON, COUPON_ICON)]
    if not main:
        return 6 if PEN_ICON in icons else 4
    first = main[0]
    cat, sub = first // 1000000, (first % 1000000) // 10000
    if cat == 1:
        return 5 if sub == 7 else 1
    if cat == 2:
        return 2 if sub == 0 else 3
    if cat == 4 and sub == 5:
        return 1                       # set item -> costume set
    if cat == 4 and sub in CAPSULE_SUBS:
        if depth < 3 and capsule_icons.get(first):
            return _capsule_sub(first, capsule_icons, item_icons, depth + 1)
        return 1
    return 4


def tabs_for(item_id, tab_data=None):
    """(MainTab, SubTab) for an item number; see the table above."""
    weapon_types, capsule_icons, item_icons = tab_data or ({}, {}, {})
    cat = item_id // 1000000
    sub = (item_id % 1000000) // 10000
    if cat == 1:          # Costume (90+ are part variants of slot sub-90)
        return 3, (sub - 90 if sub >= 90 else sub) + 2
    if cat == 2:          # Weapon; rifle-category guns (revolver, handgun, shotgun, air gun) -> Guns
        if sub == 1:
            return 2, 3 if weapon_types.get(item_id) == 3 else 2
        return 2, WEAPON_SUB_TAB.get(sub, 6)
    if cat == 3:          # Skill
        return 2, 8
    if cat == 4:          # One-time use
        if sub == 5:
            return 1, 7                                    # set items
        if sub == 2:                                       # fumbi capsules: Etc, or PEN
            return 1, 6 if _capsule_sub(item_id, capsule_icons, item_icons) == 6 else 4
        if sub in CAPSULE_SUBS:
            return 1, _capsule_sub(item_id, capsule_icons, item_icons)
        return 4, 0                                        # nickname/record/enchant tools, EXP
    if cat == 5:          # Boost
        return 4, BOOST_SUB_TAB.get(sub, 5)
    if cat == 7:          # Esper chip
        return 4, 2
    if cat == 9:          # enchant gems (not in either dump)
        return 4, 3
    return 4, 6            # coupons (6), cards (8), anything else: Items / Etc


# Categories sold per unit (stackable, one-time use): everything except costumes (1),
# weapons (2), skills (3) and esper chips (7). Matches how other S4 emulators set up the
# Season 8 shop; the official price list is not part of the client.
def is_consumable(item_id):
    return item_id // 1000000 not in (1, 2, 3, 7)



def _load_item_map():
    """Parse item.x7 into {item_id: gender}. Gender: None=0, Male=1, Female=2."""
    root = ET.parse(ITEM_X7).getroot()
    items = {}
    for it in root.findall("item"):
        key = it.get("item_key")
        if not key:
            continue
        item_id = int(key)
        base = it.find("base")
        sex = ((base.get("sex") if base is not None else "") or "").strip().lower()
        gender = 0 if sex in ("", "unisex") else (1 if sex == "man" else 2)
        items[item_id] = gender
    return items


def _load_weapon_keys():
    """Parse _eu_weapon.x7 into the set of item_keys that have ability data.

    GameDataService.LoadWeapon logs "Missing weapon for item {id}" and falls back to an
    all-zero ItemInfoWeapon for any weapon-category item in item.x7 with no matching entry
    here - it never reaches the client, but it's a reliable signal that this particular
    item's *other* client data (resource/mesh files, EU string table) is equally out of
    sync, since real client data packs are authored as one consistent unit. Filtering the
    shop on it keeps players from buying/equipping weapons that will never render.
    """
    root = ET.parse(WEAPON_X7).getroot()
    return {int(w.get("item_key")) for w in root.findall("weapon") if w.get("item_key")}


def _load_tab_data():
    """Inputs for tabs_for: weapon ability category (_eu_weapon.x7), the reward icons of
    each capsule (_eu_item_tooltip_addcapsule.x7) and each item's icon file (item.x7)."""
    weapon_types = {}
    for w in ET.parse(WEAPON_X7).getroot().findall("weapon"):
        ability = w.find("ability")
        if w.get("item_key") and ability is not None and (ability.get("category") or "").isdigit():
            weapon_types[int(w.get("item_key"))] = int(ability.get("category"))
    capsule_icons = {}
    try:
        root = ET.parse(CAPSULE_X7).getroot()
    except (OSError, ET.ParseError) as e:
        print(f"[provision] shop: no capsule tooltip ({e}); capsules go to Capsule & Set / Costume")
        root = None
    for it in (root.findall("item") if root is not None else ()):
        icon = it.find("capsule_icon")
        if it.get("id") and icon is not None:
            # attribute order is ID_1..ID_n as written in the file
            capsule_icons[int(it.get("id"))] = [
                int(v) for v in (x.strip() for x in icon.attrib.values()) if v.isdigit()]
    item_icons = {}
    for it in ET.parse(ITEM_X7).getroot().findall("item"):
        graphic = it.find("graphic")
        if it.get("item_key") and graphic is not None:
            item_icons[int(it.get("item_key"))] = (graphic.get("icon_image") or "").lower()
    return weapon_types, capsule_icons, item_icons


# ItemPeriodType (OpenS4L.Common/Enums.cs)
PERIOD_PERMANENT, PERIOD_HOURS, PERIOD_DAYS, PERIOD_UNITS = 1, 2, 3, 4
DURABILITY_PERMANENT = 2400000  # far above the client's 2400 max: owned gear never needs repair

# Price-group signatures (tuples of (PeriodType, Period)). Group 1 / price 1 is the free permanent
# price and group 2 / prices 2-5 the free 1/2/5/10 units, as before the reference shop existed:
# existing player_items and start_items point at those Ids.
SIG_PERMANENT = ((PERIOD_PERMANENT, 0),)
SIG_UNITS = ((PERIOD_UNITS, 1), (PERIOD_UNITS, 2), (PERIOD_UNITS, 5), (PERIOD_UNITS, 10))

NAME_MISSING = ("", "not trans", "none")
SET_PERIOD_RE = re.compile(r"\s*\(\s*(?:(\d+)\s*(일|시간)|내구제|영구)\s*\)\s*$")
ENG_PERIOD_RE = re.compile(r"\(\s*(?:(\d+)\s*(day|hour)s?|(permanent))\s*\)", re.I)


# ---------------------------------------------------------------------------------------------
# Operator-supplied reference shop (SHOP_REF). Nothing of it lives in this repo: the operator
# extracts it from an official-era shop dump and drops it into the data volume.
# ---------------------------------------------------------------------------------------------
def parse_sql_dump(path, tables=("shop_items", "shop_iteminfos", "shop_prices",
                                 "shop_price_groups", "shop_effects", "shop_effect_groups")):
    """Rows of the given tables from a MySQL dump: {table: [{column: value-as-str}]}.

    Handles both `INSERT INTO t VALUES (...);` per row and multi-row inserts with a column list.
    """
    txt = open(path, encoding="utf-8", errors="replace").read()
    columns = {m.group(1): re.findall(r"^\s*`(\w+)`", m.group(2), re.M)
               for m in re.finditer(r"CREATE TABLE (?:IF NOT EXISTS )?`(\w+)` \((.*?)\n\)", txt, re.S)}
    out = {}
    n = len(txt)
    for m in re.finditer(r"INSERT INTO `(\w+)`\s*(\(([^)]*)\))?\s*VALUES\s*", txt):
        table = m.group(1)
        if table not in tables:
            continue
        cols = [c.strip(" `") for c in m.group(3).split(",")] if m.group(3) else columns.get(table, [])
        rows = out.setdefault(table, [])
        i = m.end()
        while i < n:
            while txt[i] in " \t\r\n,":
                i += 1
            if txt[i] != "(":
                break
            i += 1
            vals = []
            while True:
                while txt[i] in " \t\r\n":
                    i += 1
                if txt[i] == "'":
                    j, buf = i + 1, []
                    while True:
                        ch = txt[j]
                        if ch == "\\":
                            buf.append(txt[j + 1])
                            j += 2
                        elif ch == "'" and txt[j + 1] == "'":
                            buf.append("'")
                            j += 2
                        elif ch == "'":
                            break
                        else:
                            buf.append(ch)
                            j += 1
                    vals.append("".join(buf))
                    i = j + 1
                elif txt.startswith("b'", i):
                    j = txt.index("'", i + 2)
                    vals.append(str(int(txt[i + 2:j] or "0", 2)))
                    i = j + 1
                else:
                    j = i
                    while txt[j] not in ",)":
                        j += 1
                    v = txt[i:j].strip()
                    vals.append(None if v.upper() == "NULL" else v)
                    i = j
                while txt[i] in " \t\r\n":
                    i += 1
                if txt[i] == ",":
                    i += 1
                    continue
                i += 1  # ')'
                break
            rows.append(dict(zip(cols, vals)))
    return out


def ref_from_dump(tables):
    """{item_id: entry} from one dump's shop tables. An entry has colors, unique_colors,
    periods [(PeriodType, Period)] of the item's price group and, when it has one, the effect
    group {name, preview, effects}."""
    enabled = lambda r: str(r.get("IsEnabled", "1")) not in ("0", "False", "false")
    periods = {}
    for p in tables.get("shop_prices", []):
        if enabled(p):
            ptype, period = int(p["PeriodType"]), int(p["Period"])
            periods.setdefault(p["PriceGroupId"], set()).add(
                (ptype, 0 if ptype == PERIOD_PERMANENT else period))
    groups = {g["Id"]: g for g in tables.get("shop_effect_groups", [])}
    effects = {}
    for e in tables.get("shop_effects", []):
        if int(e["Effect"] or 0):
            effects.setdefault(e["EffectGroupId"], []).append(int(e["Effect"]))
    infos = {}
    for info in tables.get("shop_iteminfos", []):
        infos.setdefault(int(info["ShopItemId"]), info)
    out = {}
    for row in tables.get("shop_items", []):
        item_id = int(row["Id"])
        entry = {"colors": int(row["Colors"] or 0), "unique_colors": int(row["UniqueColors"] or 0)}
        info = infos.get(item_id)
        if info:
            entry["periods"] = sorted(periods.get(info["PriceGroupId"], ()))
            g = groups.get(info["EffectGroupId"])
            if g:
                preview = int(g.get("PreviewEffect") or g.get("Effect") or 0)
                effs = effects.get(info["EffectGroupId"], [])
                if preview or effs:
                    entry["effect"] = {"name": g.get("Name") or "", "preview": preview, "effects": effs}
        out[item_id] = entry
    return out


def load_ref():
    """Merge the SHOP_REF sources (first one wins per item). {} when none is configured."""
    paths = [p.strip() for p in SHOP_REF.split(",") if p.strip()]
    if not paths and os.path.isfile(SHOP_REF_DEFAULT):
        paths = [SHOP_REF_DEFAULT]
    ref = {}
    for path in paths:
        try:
            if path.lower().endswith(".json"):
                with open(path, encoding="utf-8") as f:
                    part = {int(k): v for k, v in json.load(f)["items"].items()}
            else:
                part = ref_from_dump(parse_sql_dump(path))
        except (OSError, ValueError, KeyError) as e:
            print(f"[provision] shop: reference {path} unusable ({e}) - ignored")
            continue
        for k, v in part.items():
            ref.setdefault(k, v)
        print(f"[provision] shop: reference {path}: {len(part)} items")
    return ref


def extract_ref_cli(argv):
    """provision.py extract-ref DUMP.sql [...] -o shop_ref.json [--client-dir GAME_DIR]"""
    out, client_dir, dumps = "shop_ref.json", None, []
    it = iter(argv)
    for a in it:
        if a == "-o":
            out = next(it)
        elif a == "--client-dir":
            client_dir = next(it)
        else:
            dumps.append(a)
    if not dumps:
        raise SystemExit(extract_ref_cli.__doc__)
    items = {}
    for d in dumps:
        part = ref_from_dump(parse_sql_dump(d))
        for k, v in part.items():
            items.setdefault(k, v)
        print(f"{d}: {len(part)} items")
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"sources": [os.path.basename(d) for d in dumps],
                   "items": {str(k): v for k, v in sorted(items.items())}}, f, ensure_ascii=False)
    print(f"wrote {out}: {len(items)} items")
    if client_dir:
        icons = sorted(_scan_icons(os.path.join(client_dir, "resources", "image")))
        path = os.path.join(os.path.dirname(os.path.abspath(out)), "client_icons.txt")
        with open(path, "w", encoding="utf-8") as f:
            f.write("\n".join(icons) + "\n")
        print(f"wrote {path}: {len(icons)} icon names")


# ---------------------------------------------------------------------------------------------
# What the client can show
# ---------------------------------------------------------------------------------------------
def _icon_key(name):
    name = (name or "").replace("\\", "/").rsplit("/", 1)[-1]
    return os.path.splitext(name)[0].strip().lower()


def _scan_icons(image_dir):
    icons = set()
    for _root, _dirs, files in os.walk(image_dir):
        icons.update(_icon_key(f) for f in files)
    return icons


def _load_client_icons():
    """Icon names the client has, or None when neither CLIENT_ICONS nor RESOURCES_DIR exists."""
    if os.path.isfile(CLIENT_ICONS):
        with open(CLIENT_ICONS, encoding="utf-8") as f:
            return {_icon_key(l) for l in f if l.strip()}
    image_dir = os.path.join(RESOURCES_DIR, "image")
    if os.path.isdir(image_dir):
        return _scan_icons(image_dir)
    return None


def _load_item_details():
    """{item_id: (korean name, name_key, icon key)} from item.x7."""
    out = {}
    for it in ET.parse(ITEM_X7).getroot().findall("item"):
        if not it.get("item_key"):
            continue
        base, graphic = it.find("base"), it.find("graphic")
        out[int(it.get("item_key"))] = (
            (base.get("name") if base is not None else "") or "",
            ((base.get("name_key") if base is not None else "") or "").lower(),
            _icon_key(graphic.get("icon_image") if graphic is not None else ""))
    return out


def _load_eng_names():
    try:
        root = ET.parse(ITEM_STRINGS).getroot()
    except (OSError, ET.ParseError) as e:
        print(f"[provision] shop: no item string table ({e}); not filtering on names")
        return None
    return {(s.get("key") or "").lower(): (s.get("eng") or "").strip() for s in root}


def _load_known_effects():
    """(effect ids in effect_list.x7, match ids in effect_match_list.x7)."""
    def ids(path, tag, attr):
        try:
            return {int(e.get(attr)) for e in ET.parse(path).getroot().iter(tag)
                    if (e.get(attr) or "").isdigit()}
        except (OSError, ET.ParseError):
            return set()
    return (ids(EFFECT_LIST_X7, "item_effect", "effect_id"),
            ids(EFFECT_MATCH_X7, "match_key", "id"))


def _set_rank(kor_name, eng_name):
    """(family name, period rank) of a costume-set item: rank is days, permanent = 1e9."""
    m = SET_PERIOD_RE.search(kor_name)
    base = kor_name[:m.start()].strip() if m else kor_name.strip()
    if m and m.group(1):
        n = int(m.group(1))
        return base, n if m.group(2) == "일" else n / 24.0, True
    if m:
        return base, 1e9, True
    e = ENG_PERIOD_RE.search(eng_name or "")
    if e and e.group(1):
        n = int(e.group(1))
        return base, n if e.group(2).lower() == "day" else n / 24.0, False
    return base, 1e9 - 1, False


def _pick_set_variants(candidates, details, names, genders, ref):
    """Item ids of costume-set (40 5xxxx) variants to hide: per set and gender, only the
    permanent (else longest) variant stays. Sets without period variants are left alone."""
    families = {}
    for item_id in candidates:
        if item_id // 10000 != 405:
            continue
        kor, key, _ = details[item_id]
        base, rank, suffixed = _set_rank(kor, (names or {}).get(key, ""))
        families.setdefault((base, genders.get(item_id, 0)), []).append((item_id, rank, suffixed))
    hidden = set()
    for members in families.values():
        if len(members) < 2 or not any(s for _, _, s in members):
            continue
        best = max(members, key=lambda m: (m[1], m[0] in ref, -m[0]))[0]
        hidden.update(m[0] for m in members if m[0] != best)
    return hidden


# ---------------------------------------------------------------------------------------------
# Prices / effects
# ---------------------------------------------------------------------------------------------
def _price_sig(item_id, entry):
    """Price-group signature for an item: per-unit for consumables, otherwise permanent plus
    (SHOP_DAY_PERIODS) the reference's day periods."""
    periods = [tuple(p) for p in (entry or {}).get("periods", ())]
    if is_consumable(item_id):
        units = sorted({p for t, p in periods if t == PERIOD_UNITS and p > 0})
        return tuple((PERIOD_UNITS, u) for u in units) if units else SIG_UNITS
    days = sorted({p for t, p in periods if t == PERIOD_DAYS and p > 0}) if SHOP_DAY_PERIODS else []
    return SIG_PERMANENT + tuple((PERIOD_DAYS, d) for d in days)


def _sig_name(sig):
    if sig == SIG_PERMANENT:
        return "Free"
    if sig == SIG_UNITS:
        return "FreeUnits"
    if sig[0][0] == PERIOD_UNITS:
        return ("Free U" + "/".join(str(p) for _, p in sig))[:20]
    return ("Free P+" + "/".join(str(p) for t, p in sig if t == PERIOD_DAYS) + "d")[:20]


def _effect_of(entry, known_effects, known_matches):
    """(preview, name, effects) the client can show for a reference effect group, or None."""
    eff = (entry or {}).get("effect")
    if not eff:
        return None
    effects = sorted({e for e in eff.get("effects", ()) if e and e in known_effects})
    preview = int(eff.get("preview") or 0)
    if not effects and preview and preview in known_effects:
        effects = [preview]
    if not effects:
        return None
    if preview not in known_effects and preview not in known_matches:
        preview = effects[0]
    return preview, (eff.get("name") or "").strip()[:20] or str(preview), tuple(effects)


def apply_shop():
    items = _load_item_map()
    weapon_keys = _load_weapon_keys()
    tab_data = _load_tab_data()
    details = _load_item_details()
    names = _load_eng_names()
    icons = _load_client_icons()
    known_effects, known_matches = _load_known_effects()
    ref = load_ref()
    if icons is None:
        print(f"[provision] shop: no {CLIENT_ICONS} or {RESOURCES_DIR}/image - not filtering on icons")

    # What the shop lists. Hidden: weapons without _eu_weapon.x7 data (GameDataService has no
    # ability data for them and they likely won't render), items without an English name (the
    # client shows a blank entry: Korean-only content of this client build), items whose icon
    # the client lacks (unless the reference shop listed them), surplus set period variants.
    reasons = {}
    for item_id in items:
        kor, key, icon = details.get(item_id, ("", "", ""))
        if item_id // 1000000 == 2 and item_id not in weapon_keys:
            reasons[item_id] = "no weapon data"
        elif names is not None and names.get(key, "").lower() in NAME_MISSING:
            reasons[item_id] = "no English name"
        elif icons is not None and icon not in icons and item_id not in ref:
            reasons[item_id] = "no icon"
        elif SHOP_REF_ONLY and ref and item_id not in ref:
            reasons[item_id] = "not in reference shop"
    candidates = [i for i in items if i not in reasons]
    if SHOP_SET_PERIODS != "all":
        for item_id in _pick_set_variants(candidates, details, names, items, ref):
            reasons[item_id] = "set period variant"

    with connect("game") as conn, conn.cursor() as cur:
        # shop_iteminfos.Id is referenced by player_items.ShopItemInfoId, the equip-slot
        # FKs on player_characters, and start_items - all of them owned by data outside
        # this script (existing players' saved inventories/characters). A reprovision used
        # to DELETE+re-INSERT shop_iteminfos with a fresh identity value per row, so every
        # `make provision`/`bootstrap` silently orphaned every existing player's items:
        # the game server would throw building PlayerInventory and the client would get
        # disconnected right after login. Snapshot the current ShopItemId -> Id mapping and
        # reuse it below so re-running this script keeps existing IDs stable; only items
        # that are genuinely new get a fresh Id appended after the current max.
        cur.execute('SELECT "ShopItemId", "Id" FROM "shop_iteminfos"')
        existing_iteminfo_ids = dict(cur.fetchall())
        next_id = max(existing_iteminfo_ids.values(), default=0) + 1
        # Owned items (and starter gear) must stay in the shop data even when hidden, or the
        # game server drops them at login; they are kept with their listing switched off.
        cur.execute('SELECT DISTINCT i."ShopItemId" FROM "player_items" pi '
                    'JOIN "shop_iteminfos" i ON i."Id" = pi."ShopItemInfoId"')
        keep = {r[0] for r in cur.fetchall()} | set(_starter_items())
        old_prices = {}
        cur.execute('SELECT "Id", "PeriodType", "Period" FROM "shop_prices"')
        for pid, ptype, period in cur.fetchall():
            old_prices[pid] = (ptype, period)

        shop = {i: i not in reasons for i in items if i not in reasons or i in keep}

        # Price groups: one per distinct period signature; 1 = permanent and 2 = 1/2/5/10 units
        # always exist so their Ids stay what they always were.
        sig_of = {i: _price_sig(i, ref.get(i)) for i in shop}
        sigs = [SIG_PERMANENT, SIG_UNITS] + sorted(set(sig_of.values()) - {SIG_PERMANENT, SIG_UNITS})
        group_of_sig = {s: n + 1 for n, s in enumerate(sigs)}
        prices = []   # (price id, group id, period type, period, durability)
        for sig in sigs:
            for ptype, period in sig:
                prices.append((len(prices) + 1, group_of_sig[sig], ptype, period,
                               1 if ptype == PERIOD_UNITS else DURABILITY_PERMANENT))

        # Effect groups: the client gets them as (PreviewEffect, effects) and finds an item's
        # group by its PreviewEffect, so previews are unique per group.
        effect_groups = {0: ("None", ())}
        effect_of = {}
        conflicts = 0
        for i in shop:
            e = _effect_of(ref.get(i), known_effects, known_matches)
            if not e:
                continue
            preview, name, effs = e
            if preview in effect_groups and effect_groups[preview][1] != effs:
                conflicts += 1
            else:
                effect_groups.setdefault(preview, (name, effs))
            effect_of[i] = preview
        group_of_preview = {p: n + 1 for n, p in enumerate(sorted(effect_groups))}

        # Disable FK checks for the reload (like FOREIGN_KEY_CHECKS=0 in MySQL).
        cur.execute("SET session_replication_role = 'replica'")
        try:
            for t in ("start_items", "shop_iteminfos", "shop_items", "shop_prices",
                      "shop_price_groups", "shop_effects", "shop_effect_groups", "shop_version"):
                cur.execute(f'DELETE FROM "{t}"')
            effect_row = 0
            for preview, gid in sorted(group_of_preview.items(), key=lambda x: x[1]):
                name, effs = effect_groups[preview]
                cur.execute('INSERT INTO "shop_effect_groups" ("Id", "Name", "PreviewEffect") '
                            'VALUES (%s, %s, %s)', (gid, name, preview))
                for effect in effs:
                    effect_row += 1
                    cur.execute('INSERT INTO "shop_effects" ("Id", "EffectGroupId", "Effect") '
                                'VALUES (%s, %s, %s)', (effect_row, gid, effect))
            for sig in sigs:
                cur.execute('INSERT INTO "shop_price_groups" ("Id", "Name", "PriceType") '
                            'VALUES (%s, %s, 1)', (group_of_sig[sig], _sig_name(sig)))  # 1 = PEN
            for pid, gid, ptype, period, durability in prices:
                cur.execute(
                    'INSERT INTO "shop_prices" '
                    '("Id", "PriceGroupId", "PeriodType", "Period", "Price", "IsRefundable", "Durability", "IsEnabled") '
                    "VALUES (%s, %s, %s, %s, 0, TRUE, %s, TRUE)", (pid, gid, ptype, period, durability))
            for item_id, listed in sorted(shop.items()):
                gender = items[item_id]
                main, sub = tabs_for(item_id, tab_data)
                entry = ref.get(item_id) or {}
                cur.execute(
                    'INSERT INTO "shop_items" '
                    '("Id", "RequiredGender", "RequiredLicense", "Colors", "UniqueColors", "RequiredLevel", '
                    '"LevelLimit", "RequiredMasterLevel", "IsOneTimeUse", "IsDestroyable", "MainTab", "SubTab") '
                    "VALUES (%s, %s, 0, %s, %s, 0, 0, 0, FALSE, TRUE, %s, %s)",
                    (item_id, gender, min(255, int(entry.get("colors", 0))),
                     min(255, int(entry.get("unique_colors", 0))), main, sub))
                # IsOneTimeUse is false in official shop data, even for capsules (the client
                # hangs on "Requesting" buying a capsule flagged one-time-use)
                iteminfo_id = existing_iteminfo_ids.get(item_id)
                if iteminfo_id is None:
                    iteminfo_id = next_id
                    next_id += 1
                cur.execute(
                    'INSERT INTO "shop_iteminfos" '
                    '("Id", "ShopItemId", "PriceGroupId", "EffectGroupId", "DiscountPercentage", "IsEnabled") '
                    "VALUES (%s, %s, %s, %s, 0, %s)",
                    (iteminfo_id, item_id, group_of_sig[sig_of[item_id]],
                     group_of_preview[effect_of.get(item_id, 0)], listed))
            # Explicit Ids were inserted above, so bump the identity sequences past the highest
            # one used or the next auto-assigned (non-reprovision) insert would collide with it.
            for table, top in (("shop_iteminfos", next_id - 1), ("shop_prices", len(prices)),
                               ("shop_price_groups", len(sigs)), ("shop_effects", effect_row),
                               ("shop_effect_groups", len(group_of_preview))):
                cur.execute('SELECT setval(pg_get_serial_sequence(%s, %s), %s)', (table, "Id", max(top, 1)))
            _migrate_player_items(cur, old_prices, prices)
            cur.execute('INSERT INTO "shop_version" ("Id", "Version") VALUES (1, %s)',
                        (str(int(time.time())),))
        finally:
            cur.execute("SET session_replication_role = 'origin'")

    listed = sum(shop.values())
    print(f"[provision] shop: {listed} items listed, {len(shop) - listed} kept unlisted (owned/starter), "
          f"{len(sigs)} price groups, {len(group_of_preview)} effect groups"
          + (f" ({conflicts} preview clashes kept the first group)" if conflicts else "")
          + (f", reference covers {sum(1 for i in shop if i in ref)}" if ref else " (no reference shop)"))
    by_reason = {}
    for item_id, why in reasons.items():
        by_reason.setdefault(why, []).append(item_id)
    for why, ids in sorted(by_reason.items()):
        print(f"[provision] shop: {len(ids)} hidden - {why}"
              + (f": {sorted(ids)}" if why == "no weapon data" else ""))
    return shop


def _migrate_player_items(cur, old_prices, prices):
    """Point every owned item at a price of its item's (possibly new) price group, keep its
    period where the group still offers it, give it its listing's stat effects if it has none,
    and fold duplicate per-unit rows into one stack (the client shows a per-unit item's
    durability as its count). Safe to re-run."""
    by_group = {}
    for pid, gid, ptype, period, durability in prices:
        by_group.setdefault(gid, []).append((pid, ptype, period, durability))
    cur.execute('SELECT i."Id", i."PriceGroupId", COALESCE(json_agg(e."Effect" ORDER BY e."Effect") '
                'FILTER (WHERE e."Effect" <> 0), %s) FROM "shop_iteminfos" i '
                'LEFT JOIN "shop_effects" e ON e."EffectGroupId" = i."EffectGroupId" GROUP BY 1, 2',
                ("[]",))
    infos = {}
    for info_id, gid, effects in cur.fetchall():
        if isinstance(effects, str):
            effects = json.loads(effects)
        infos[info_id] = (gid, [int(e) for e in effects or ()])
    cur.execute('SELECT "Id", "ShopItemInfoId", "ShopPriceId", "Durability", "Effects" FROM "player_items"')
    moved = effected = 0
    for item_id, info_id, price_id, durability, effects in cur.fetchall():
        if info_id not in infos:
            continue
        gid, group_effects = infos[info_id]
        choices = by_group[gid]
        old = old_prices.get(price_id)
        same = [c for c in choices if old and (c[1], c[2]) == tuple(old)]
        new = (same or [c for c in choices if c[1] == PERIOD_PERMANENT]
               or sorted(choices, key=lambda c: c[2]))[0]
        sets, args = [], []
        if new[0] != price_id:
            sets.append('"ShopPriceId" = %s')
            args.append(new[0])
            moved += 1
            if old and old[0] == PERIOD_UNITS and new[1] != PERIOD_UNITS:
                sets.append('"Durability" = %s')   # a stack count is no durability
                args.append(new[3])
        if group_effects and (effects or "").strip() in ("", "[]", "null"):
            sets.append('"Effects" = %s')
            args.append(json.dumps(group_effects))
            effected += 1
        if sets:
            cur.execute(f'UPDATE "player_items" SET {", ".join(sets)} WHERE "Id" = %s', (*args, item_id))
    # Rows from before stacking carry a permanent-price durability, so anything implausibly
    # large counts as one unit.
    cur.execute(
        'WITH u AS ('
        '  SELECT pi.* FROM "player_items" pi JOIN "shop_prices" p ON p."Id" = pi."ShopPriceId"'
        '  WHERE p."PeriodType" = %s),'
        ' g AS ('
        '  SELECT "PlayerId", "ShopItemInfoId", "Color", MIN("Id") AS keep,'
        '         SUM(CASE WHEN "Durability" BETWEEN 1 AND 100000 THEN "Durability" ELSE 1 END) AS n'
        '  FROM u GROUP BY 1, 2, 3)'
        ' UPDATE "player_items" pi SET "Durability" = g.n FROM g WHERE pi."Id" = g.keep',
        (PERIOD_UNITS,))
    cur.execute(
        'DELETE FROM "player_items" pi USING "shop_prices" p '
        'WHERE p."Id" = pi."ShopPriceId" AND p."PeriodType" = %s AND EXISTS ('
        '  SELECT 1 FROM "player_items" o JOIN "shop_prices" op ON op."Id" = o."ShopPriceId"'
        '  WHERE op."PeriodType" = %s AND o."PlayerId" = pi."PlayerId"'
        '  AND o."ShopItemInfoId" = pi."ShopItemInfoId" AND o."Color" = pi."Color" AND o."Id" < pi."Id")',
        (PERIOD_UNITS, PERIOD_UNITS))
    if moved or effected:
        print(f"[provision] shop: player items: {moved} moved to a valid price, {effected} given their stat effects")


def _starter_items():
    """Regular counterparts of every "character creation" item (marked "(캐릭터 생성)" in
    item.x7): the gear of the starter outfits the create-character screen offers."""
    # Map each item to its (base_name, gender_key) so we can pair a creation variant
    # ("실크레인 (캐릭터 생성)") with its regular counterpart ("실크레인").
    items = {}
    for it in ET.parse(ITEM_X7).getroot().findall("item"):
        key = it.get("item_key")
        if not key:
            continue
        base = it.find("base")
        name = (base.get("name") if base is not None else "") or ""
        sex = ((base.get("sex") if base is not None else "") or "").strip().lower()
        items[int(key)] = (name, sex)

    def strip_creation(n):
        return re.sub(r"\s*\(캐릭터 생성.*$", "", n).strip()

    by_base = {}
    for iid, (name, sex) in items.items():
        by_base.setdefault((strip_creation(name), sex), []).append(iid)

    starters = set()
    for iid, (name, sex) in items.items():
        if "캐릭터 생성" not in name:
            continue
        regulars = [i for i in by_base.get((strip_creation(name), sex), []) if i != iid]
        starters.add(regulars[0] if regulars else iid)
    return sorted(starters)


def ensure_start_items():
    """Seed `start_items` (default gear granted on character creation).

    On a fresh character the client's create-character screen offers 10 starter outfits
    (5 male + 5 female) and sends the chosen outfit's item numbers in
    CharacterFirstCreateReqMessage.Items. The Game server only grants an item if a
    matching row exists in `start_items` (see AuthenticationHandler.OnHandle
    CharacterFirstCreateReqMessage). Without it the character is created but owns no
    equipment, so the client's character preview is empty and it won't let you enter a
    channel. We seed the regular counterparts of every "character creation" item, so
    whichever outfit is picked its gear is granted.

    start_items.ShopItemInfoId references shop_iteminfos.Id (the listing), and ShopPriceId
    must be a price of that listing's price group: its permanent one.
    apply_shop empties the table, so this re-seeds it on every run.
    """
    starters = _starter_items()
    if not starters:
        print("[provision] start_items: no starter items found in item.x7 (skipped)")
        return

    with connect("game") as conn, conn.cursor() as cur:
        cur.execute('SELECT COUNT(*) FROM "start_items"')
        count = cur.fetchone()[0]
        if count > 0:
            print(f"[provision] start_items: {count} already present (skipped)")
            return
        # start_items requires shop_iteminfos to exist (created by apply_shop).
        seeded = 0
        for item_id in starters:
            cur.execute(
                'SELECT i."Id", p."Id" FROM "shop_iteminfos" i JOIN "shop_prices" p '
                'ON p."PriceGroupId" = i."PriceGroupId" WHERE i."ShopItemId" = %s '
                'ORDER BY (p."PeriodType" <> %s), p."Id" LIMIT 1', (item_id, PERIOD_PERMANENT))
            row = cur.fetchone()
            if row is None:
                continue
            cur.execute(
                'INSERT INTO "start_items" ("ShopItemInfoId", "ShopPriceId", "Color", "RequiredSecurityLevel") '
                "VALUES (%s, %s, 0, 0)", row)
            seeded += 1
    print(f"[provision] start_items: {seeded} starter items seeded (5 male + 5 female outfits)")


def ensure_bots():
    """Create BOT_COUNT regular (SecurityLevel 0) accounts named {prefix}0..{prefix}N-1.

    The load-bot harness needs one account per concurrent bot, because the game server
    rejects a second concurrent login for the same account (TerminateOtherConnection).
    Idempotent: existing accounts are left alone, only missing ones are created.
    """
    if BOT_COUNT <= 0:
        return 0
    with connect("auth") as conn, conn.cursor() as cur:
        existing = set()
        cur.execute('SELECT "Username" FROM "accounts" WHERE "Username" LIKE %s', (BOT_PREFIX + "%",))
        for row in cur.fetchall():
            existing.add(row[0])
        created = 0
        for i in range(BOT_COUNT):
            username = f"{BOT_PREFIX}{i}"
            if username in existing:
                continue
            salt = os.urandom(PBKDF2_DKLEN)
            key = hashlib.pbkdf2_hmac("sha1", BOT_PASSWORD.encode(), salt,
                                      PBKDF2_ITERATIONS, dklen=PBKDF2_DKLEN)
            cur.execute(
                'INSERT INTO "accounts" ("Username", "Nickname", "Password", "Salt", "SecurityLevel") '
                "VALUES (%s, NULL, %s, %s, %s)",
                (username,
                 base64.b64encode(key).decode(),
                 base64.b64encode(salt).decode(),
                 0),  # regular user
            )
            created += 1
        if created:
            print(f"[provision] bots: created {created} account(s) ({BOT_PREFIX}0..{BOT_PREFIX}{BOT_COUNT-1})")
        else:
            print(f"[provision] bots: all {BOT_COUNT} account(s) already present (skipped)")
        return created


def main():
    print(f"[provision] connecting to {DB_HOST}:5432 as postgres...")
    wait_for_schema()
    if ONLY_BOTS:
        ensure_bots()
        print("[provision] done (bots only).")
        return
    ensure_admin()
    ensure_bots()
    ensure_channels()
    apply_shop()
    ensure_start_items()
    print("[provision] done. Restart the game server (make bootstrap/provision does this) to load the shop.")


if __name__ == "__main__":
    if sys.argv[1:2] == ["extract-ref"]:
        extract_ref_cli(sys.argv[2:])
    else:
        main()
