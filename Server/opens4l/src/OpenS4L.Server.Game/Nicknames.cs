using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OpenS4L.Common.Configuration;
using OpenS4L.Database;
using OpenS4L.Database.Auth;

namespace OpenS4L.Server.Game
{
    public enum NicknameCheckResult
    {
        OK,
        TooShort,
        TooLong,
        InvalidCharacters,
        Taken
    }

    /// <summary>
    /// Nickname rules (NickRestrictions) and the nickname change items.
    ///
    /// nickname_history rows hold a player's previous nickname. A row with an ExpireDate
    /// (unix seconds, not -1) is a temporary change that is still pending: the old nickname stays
    /// reserved until then and is restored when the player logs in after it, or when the change is
    /// cancelled. Finished/permanent changes have ExpireDate null. ExpireDate -1 reserves a
    /// nickname forever (not written by the server).
    /// </summary>
    public static class Nicknames
    {
        public static NicknameCheckResult CheckFormat(string nickname, NickRestrictionOptions restrictions)
        {
            restrictions = restrictions ?? new NickRestrictionOptions();
            if (string.IsNullOrWhiteSpace(nickname) || nickname.Length < restrictions.MinLength)
                return NicknameCheckResult.TooShort;

            if (nickname.Length > restrictions.MaxLength)
                return NicknameCheckResult.TooLong;

            if (!restrictions.WhitespaceAllowed && nickname.Contains(" ") ||
                restrictions.AsciiOnly && Encoding.UTF8.GetByteCount(nickname) != nickname.Length)
            {
                return NicknameCheckResult.InvalidCharacters;
            }

            // check for repeating chars example: (AAAHello, HeLLLLo)
            var maxRepeat = restrictions.MaxRepeat;
            if (maxRepeat > 0)
            {
                var counter = 1;
                var current = nickname[0];
                for (var i = 1; i < nickname.Length; i++)
                {
                    if (current != nickname[i])
                    {
                        if (counter > maxRepeat)
                            return NicknameCheckResult.InvalidCharacters;

                        counter = 0;
                        current = nickname[i];
                    }

                    ++counter;
                }

                if (counter > maxRepeat)
                    return NicknameCheckResult.InvalidCharacters;
            }

            return NicknameCheckResult.OK;
        }

        public static async Task<NicknameCheckResult> CheckAsync(DatabaseService databaseService, string nickname,
            NickRestrictionOptions restrictions)
        {
            var result = CheckFormat(nickname, restrictions);
            if (result != NicknameCheckResult.OK)
                return result;

            return await IsTakenAsync(databaseService, nickname)
                ? NicknameCheckResult.Taken
                : NicknameCheckResult.OK;
        }

        /// <summary>In use by an account or reserved in nickname_history.</summary>
        public static async Task<bool> IsTakenAsync(DatabaseService databaseService, string nickname,
            long? exceptAccountId = null)
        {
            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            using (var db = databaseService.Open<AuthContext>())
            {
                var nickExists = await db.Accounts.AnyAsync(x =>
                    x.Nickname == nickname && (exceptAccountId == null || x.Id != exceptAccountId));
                var nickReserved = await db.Nicknames.AnyAsync(x =>
                    x.Nickname == nickname && (x.ExpireDate == -1 || x.ExpireDate > now) &&
                    (exceptAccountId == null || x.AccountId != exceptAccountId));
                return nickExists || nickReserved;
            }
        }

        public static async Task<bool> HasPendingChangeAsync(DatabaseService databaseService, ulong accountId)
        {
            var id = (int)accountId;
            using (var db = databaseService.Open<AuthContext>())
            {
                return await db.Nicknames.AnyAsync(x =>
                    x.AccountId == id && x.ExpireDate != null && x.ExpireDate != -1);
            }
        }

        /// <summary>
        /// Changes the nickname and records the old one. days null = permanent change, otherwise the
        /// old nickname comes back after that many days.
        /// </summary>
        public static async Task<bool> ChangeAsync(DatabaseService databaseService, ulong accountId,
            string nickname, int? days)
        {
            var id = (int)accountId;
            using (var db = databaseService.Open<AuthContext>())
            {
                var account = await db.Accounts.FirstOrDefaultAsync(x => x.Id == id);
                if (account == null)
                    return false;

                db.Nicknames.Add(new NicknameHistoryEntity
                {
                    AccountId = id,
                    Nickname = account.Nickname,
                    ExpireDate = days == null
                        ? (long?)null
                        : DateTimeOffset.Now.AddDays(days.Value).ToUnixTimeSeconds()
                });
                account.Nickname = nickname;
                await db.SaveChangesAsync();
                return true;
            }
        }

        /// <summary>
        /// Ends the pending temporary change: restores the old nickname and marks the change finished.
        /// If someone else took the old nickname after the reservation ran out, an expired change
        /// keeps the current nickname and a cancel fails.
        /// onlyExpired restricts it to changes past their expire date (login), otherwise it cancels.
        /// Returns the nickname the account has afterwards, or null when there was nothing to end.
        /// </summary>
        public static async Task<string> RestoreAsync(DatabaseService databaseService, ulong accountId,
            bool onlyExpired)
        {
            var id = (int)accountId;
            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            using (var db = databaseService.Open<AuthContext>())
            {
                var pending = await db.Nicknames
                    .Where(x => x.AccountId == id && x.ExpireDate != null && x.ExpireDate != -1)
                    .OrderByDescending(x => x.Id)
                    .FirstOrDefaultAsync();
                if (pending == null || onlyExpired && pending.ExpireDate > now)
                    return null;

                var account = await db.Accounts.FirstOrDefaultAsync(x => x.Id == id);
                if (account == null)
                    return null;

                var oldNickname = pending.Nickname;
                var taken = await db.Accounts.AnyAsync(x => x.Nickname == oldNickname && x.Id != id) ||
                    await db.Nicknames.AnyAsync(x => x.Nickname == oldNickname && x.AccountId != id &&
                        (x.ExpireDate == -1 || x.ExpireDate > now));

                // A cancel fails and keeps the change pending; an expired change keeps the
                // temporary nickname for good
                if (taken && !onlyExpired)
                    return null;

                pending.ExpireDate = null;
                if (!taken)
                    account.Nickname = oldNickname;

                await db.SaveChangesAsync();
                return account.Nickname;
            }
        }
    }
}
