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
"""
import base64
import hashlib
import os
import time
import xml.etree.ElementTree as ET

import psycopg2

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


# Free per-unit prices (Id, units). Id 1 stays the free permanent price.
UNIT_PRICES = ((2, 1), (3, 2), (4, 5), (5, 10))


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


def apply_shop():
    items = _load_item_map()
    weapon_keys = _load_weapon_keys()
    tab_data = _load_tab_data()

    # Weapon-category items (item_id // 1000000 == 2) with no _eu_weapon.x7 entry: see
    # _load_weapon_keys. Skip them instead of selling something that can't render -
    # existing players who already own one are handled by PlayerInventory dropping the
    # now-orphaned item at login (see PlayerInventory.cs).
    missing_weapon_data = sorted(
        item_id for item_id in items if item_id // 1000000 == 2 and item_id not in weapon_keys
    )
    items = {k: v for k, v in items.items() if k not in missing_weapon_data}

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

        # Disable FK checks for the reload (like FOREIGN_KEY_CHECKS=0 in MySQL).
        cur.execute("SET session_replication_role = 'replica'")
        try:
            for t in ("start_items", "shop_iteminfos", "shop_items", "shop_prices",
                      "shop_price_groups", "shop_effects", "shop_effect_groups", "shop_version"):
                cur.execute(f'DELETE FROM "{t}"')
            cur.execute('INSERT INTO "shop_effect_groups" ("Id", "Name", "PreviewEffect") VALUES (1, %s, 0)', ("None",))
            cur.execute('INSERT INTO "shop_price_groups" ("Id", "Name", "PriceType") VALUES (1, %s, 1)', ("Free",))  # 1 = PEN
            cur.execute(
                'INSERT INTO "shop_prices" '
                '("Id", "PriceGroupId", "PeriodType", "Period", "Price", "IsRefundable", "Durability", "IsEnabled") '
                "VALUES (1, 1, 1, 0, 0, TRUE, 2400000, TRUE)")  # permanent, free, high durability
            cur.execute('INSERT INTO "shop_price_groups" ("Id", "Name", "PriceType") VALUES (2, %s, 1)', ("FreeUnits",))
            for price_id, units in UNIT_PRICES:
                cur.execute(
                    'INSERT INTO "shop_prices" '
                    '("Id", "PriceGroupId", "PeriodType", "Period", "Price", "IsRefundable", "Durability", "IsEnabled") '
                    "VALUES (%s, 2, 4, %s, 0, TRUE, 1, TRUE)", (price_id, units))  # 4 = Units
            for item_id, gender in sorted(items.items()):
                main, sub = tabs_for(item_id, tab_data)
                cur.execute(
                    'INSERT INTO "shop_items" '
                    '("Id", "RequiredGender", "RequiredLicense", "Colors", "UniqueColors", "RequiredLevel", '
                    '"LevelLimit", "RequiredMasterLevel", "IsOneTimeUse", "IsDestroyable", "MainTab", "SubTab") '
                    "VALUES (%s, %s, 0, 0, 0, 0, 0, 0, FALSE, TRUE, %s, %s)",
                    (item_id, gender, main, sub))  # IsOneTimeUse is false in official shop data, even for capsules
                iteminfo_id = existing_iteminfo_ids.get(item_id)
                if iteminfo_id is None:
                    iteminfo_id = next_id
                    next_id += 1
                cur.execute(
                    'INSERT INTO "shop_iteminfos" '
                    '("Id", "ShopItemId", "PriceGroupId", "EffectGroupId", "DiscountPercentage", "IsEnabled") '
                    "VALUES (%s, %s, %s, 1, 0, TRUE)",
                    (iteminfo_id, item_id, 2 if is_consumable(item_id) else 1))
            # Explicit Ids were inserted above, so bump the identity sequence past the
            # highest one used or the next auto-assigned (non-reprovision) insert would
            # collide with it.
            cur.execute('SELECT setval(pg_get_serial_sequence(%s, %s), %s)',
                        ("shop_iteminfos", "Id", next_id - 1 if next_id > 1 else 1))
            # Owned items must reference a price in their item's price group, or the game server
            # can't load them: move consumables to 1 unit and everything else to permanent.
            cur.execute(
                'UPDATE "player_items" pi SET "ShopPriceId" = CASE WHEN i."PriceGroupId" = 2 THEN 2 ELSE 1 END '
                'FROM "shop_iteminfos" i WHERE i."Id" = pi."ShopItemInfoId"')
            # Per-unit items stack (the client shows durability as the count): fold duplicate rows
            # into the oldest one. Rows from before stacking carry a permanent-price durability, so
            # anything implausibly large counts as one unit. Safe to re-run.
            cur.execute(
                'WITH g AS ('
                '  SELECT pi."PlayerId", pi."ShopItemInfoId", pi."Color", MIN(pi."Id") AS keep,'
                '         SUM(CASE WHEN pi."Durability" BETWEEN 1 AND 100000 THEN pi."Durability" ELSE 1 END) AS n'
                '  FROM "player_items" pi JOIN "shop_iteminfos" i ON i."Id" = pi."ShopItemInfoId"'
                '  WHERE i."PriceGroupId" = 2 GROUP BY 1, 2, 3)'
                ' UPDATE "player_items" pi SET "Durability" = g.n FROM g WHERE pi."Id" = g.keep')
            cur.execute(
                'DELETE FROM "player_items" pi USING "shop_iteminfos" i '
                'WHERE i."Id" = pi."ShopItemInfoId" AND i."PriceGroupId" = 2 AND EXISTS ('
                '  SELECT 1 FROM "player_items" o WHERE o."PlayerId" = pi."PlayerId"'
                '  AND o."ShopItemInfoId" = pi."ShopItemInfoId" AND o."Color" = pi."Color" AND o."Id" < pi."Id")')
            cur.execute('INSERT INTO "shop_version" ("Id", "Version") VALUES (1, %s)',
                        (str(int(time.time())),))
        finally:
            cur.execute("SET session_replication_role = 'origin'")
    print(f"[provision] shop: {len(items)} items enabled (free; consumables per unit, the rest permanent)")
    if missing_weapon_data:
        print(f"[provision] shop: {len(missing_weapon_data)} weapon(s) skipped - no _eu_weapon.x7 "
              f"data (likely won't render): {missing_weapon_data}")


def ensure_start_items():
    """Seed `start_items` (default gear granted on character creation).

    On a fresh character the client's create-character screen offers 10 starter outfits
    (5 male + 5 female) and sends the chosen outfit's item numbers in
    CharacterFirstCreateReqMessage.Items. The Game server only grants an item if a
    matching row exists in `start_items` (see AuthenticationHandler.OnHandle
    CharacterFirstCreateReqMessage). Without it the character is created but owns no
    equipment, so the client's character preview is empty and it won't let you enter a
    channel. We seed the regular counterparts of every "character creation" item
    (marked "(캐릭터 생성)" in item.x7), so whichever outfit is picked its gear is granted.

    start_items.ShopItemInfoId references shop_iteminfos.Id (the buyable listing), and
    ShopPriceId references shop_prices.Id (=1, the free permanent price seeded above).
    Idempotent: skips if rows already present.
    """
    root = ET.parse(ITEM_X7).getroot()
    import re as _re
    # Map each item to its (base_name, gender_key) so we can pair a creation variant
    # ("실크레인 (캐릭터 생성)") with its regular counterpart ("실크레인").
    items = {}
    for it in root.findall("item"):
        key = it.get("item_key")
        if not key:
            continue
        base = it.find("base")
        name = (base.get("name") if base is not None else "") or ""
        sex = ((base.get("sex") if base is not None else "") or "").strip().lower()
        items[int(key)] = (name, sex)

    def strip_creation(n):
        return _re.sub(r"\s*\(캐릭터 생성.*$", "", n).strip()

    by_base = {}
    for iid, (name, sex) in items.items():
        by_base.setdefault((strip_creation(name), sex), []).append(iid)

    starters = set()
    for iid, (name, sex) in items.items():
        if "캐릭터 생성" not in name:
            continue
        base = strip_creation(name)
        regulars = [i for i in by_base.get((base, sex), []) if i != iid]
        starters.add(regulars[0] if regulars else iid)

    starters = sorted(starters)
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
        for item_id in starters:
            cur.execute('SELECT "Id" FROM "shop_iteminfos" WHERE "ShopItemId"=%s', (item_id,))
            row = cur.fetchone()
            if row is None:
                continue
            cur.execute(
                'INSERT INTO "start_items" ("ShopItemInfoId", "ShopPriceId", "Color", "RequiredSecurityLevel") '
                "VALUES (%s, 1, 0, 0)",
                (row[0],))
    print(f"[provision] start_items: {len(starters)} starter items seeded (5 male + 5 female outfits)")


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
    main()
