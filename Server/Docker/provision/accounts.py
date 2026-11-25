"""Player account maintenance for the OpenS4L stack. The auth server has no sign-up, so the
operator creates accounts here. Runs in the provision image (make prod-account / prod-secure).

  accounts.py set <username>        create the account, or reset its password if it exists
  accounts.py bots                  reset the password of every existing {BOT_PREFIX}N account

The password comes from ACCOUNT_PASSWORD (set) or BOT_PASSWORD (bots), never from argv, so it
does not show up in the process list. Hashing matches provision.py and the auth server.
"""
import base64
import hashlib
import os
import re
import sys

from provision import BOT_PREFIX, PBKDF2_DKLEN, PBKDF2_ITERATIONS, connect


def hash_password(password):
    salt = os.urandom(PBKDF2_DKLEN)
    key = hashlib.pbkdf2_hmac("sha1", password.encode(), salt, PBKDF2_ITERATIONS, dklen=PBKDF2_DKLEN)
    return base64.b64encode(key).decode(), base64.b64encode(salt).decode()


def require(name):
    value = os.getenv(name, "")
    if len(value) < 8:
        raise SystemExit(f"[accounts] {name} must be set (at least 8 characters)")
    return value


def set_account(username):
    username = username.lower()
    if not re.fullmatch(r"[a-z0-9_]{3,16}", username):
        raise SystemExit("[accounts] username: 3-16 characters, a-z 0-9 _")
    pw, salt = hash_password(require("ACCOUNT_PASSWORD"))
    with connect("auth") as conn, conn.cursor() as cur:
        cur.execute('UPDATE "accounts" SET "Password"=%s, "Salt"=%s WHERE "Username"=%s',
                    (pw, salt, username))
        if cur.rowcount:
            print(f"[accounts] '{username}': password reset")
            return
        cur.execute('INSERT INTO "accounts" ("Username", "Nickname", "Password", "Salt", "SecurityLevel") '
                    "VALUES (%s, NULL, %s, %s, 0)", (username, pw, salt))
        print(f"[accounts] '{username}': created (regular player)")


def reset_bots():
    password = require("BOT_PASSWORD")
    with connect("auth") as conn, conn.cursor() as cur:
        cur.execute('SELECT "Username" FROM "accounts" WHERE "Username" ~ %s',
                    ("^" + re.escape(BOT_PREFIX) + "[0-9]+$",))
        names = [r[0] for r in cur.fetchall()]
        for name in names:
            pw, salt = hash_password(password)
            cur.execute('UPDATE "accounts" SET "Password"=%s, "Salt"=%s WHERE "Username"=%s',
                        (pw, salt, name))
        print(f"[accounts] {len(names)} bot account(s): password reset")


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "set":
        set_account(sys.argv[2])
    elif len(sys.argv) == 2 and sys.argv[1] == "bots":
        reset_bots()
    else:
        raise SystemExit(__doc__)
