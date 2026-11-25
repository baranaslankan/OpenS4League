using System.Net;

namespace OpenS4L.Plugins.WebApi
{
    public class WebApiOptions
    {
        public string Listener { get; set; }

        /// <summary>
        /// When set, every POST /admin/* request must carry it in the X-Admin-Key header (401 otherwise).
        /// Empty keeps the admin endpoints open, which is only safe while the port stays on localhost.
        /// Overridable from the environment as OPENS4L_WEBAPI_ADMINKEY.
        /// </summary>
        public string AdminKey { get; set; }
    }
}
