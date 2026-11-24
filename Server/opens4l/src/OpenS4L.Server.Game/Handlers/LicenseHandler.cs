using System.Threading.Tasks;
using Logging;
using OpenS4L.Network;
using OpenS4L.Network.Message.Game;
using OpenS4L.Server.Game.Rules;
using ProudNet;

namespace OpenS4L.Server.Game.Handlers
{
    /// <summary>
    /// License requests. Every license is force-granted at login (Player.SendAccountInformation)
    /// and no license progress is stored, so these only keep the client's license list in sync.
    /// This client build has no LicenseGainAck/LicenseExerciseAck; LicenseMyInfoAck is the only
    /// license message the server can send.
    /// </summary>
    internal class LicenseHandler
        : IHandle<LicenseGainReqMessage>, IHandle<LicenseExerciseReqMessage>
    {
        private readonly ILogger _logger;

        public LicenseHandler(ILogger<LicenseHandler> logger)
        {
            _logger = logger;
        }

        [Firewall(typeof(MustBeLoggedIn))]
        public async Task<bool> OnHandle(MessageContext context, LicenseGainReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;
            plr.AddContextToLogger(_logger).Debug("License gain {License} (already granted)", message.License);

            // Already granted: answer with the full list again so the client's view stays right
            plr.SendLicenses();
            return true;
        }

        [Firewall(typeof(MustBeLoggedIn))]
        public async Task<bool> OnHandle(MessageContext context, LicenseExerciseReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;

            // Sent when the player starts a license exercise (training). There is no ack in this
            // client build and nothing to unlock since every license is granted.
            plr.AddContextToLogger(_logger).Debug("License exercise {License}", message.License);
            return true;
        }
    }
}
