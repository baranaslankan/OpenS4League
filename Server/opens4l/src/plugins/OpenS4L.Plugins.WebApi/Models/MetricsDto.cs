using System.Collections.Generic;

namespace OpenS4L.Plugins.WebApi.Models
{
    /// <summary>
    /// GET /metrics: aggregate numbers that are safe to show to anyone (the public status page).
    /// No nicknames, account ids or room passwords, unlike /players and /rooms.
    /// </summary>
    public class MetricsDto
    {
        public long ServerTime { get; set; }
        public long Uptime { get; set; }
        public int PlayersOnline { get; set; }
        public int PeakPlayers { get; set; }
        public MetricsChannelDto[] Channels { get; set; }
        public MetricsRoomsDto Rooms { get; set; }
        public MetricsProcessDto Process { get; set; }
    }

    public class MetricsChannelDto
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public int PlayersOnline { get; set; }
        public int PlayerLimit { get; set; }
        public int Rooms { get; set; }
    }

    public class MetricsRoomsDto
    {
        public int Total { get; set; }
        public int Playing { get; set; }
        public int PlayersInRooms { get; set; }
        /// <summary>Room count per game rule name (Deathmatch, Touchdown, ...).</summary>
        public Dictionary<string, int> ByRule { get; set; }
        /// <summary>Room count per game state name (Waiting, Loading, Playing, Result).</summary>
        public Dictionary<string, int> ByState { get; set; }
    }

    public class MetricsProcessDto
    {
        /// <summary>CPU use of the game server process since the previous /metrics call, in percent of all cores.</summary>
        public double CpuPercent { get; set; }
        public int ProcessorCount { get; set; }
        public double WorkingSetMb { get; set; }
        public double ManagedHeapMb { get; set; }
        public int Threads { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public string Runtime { get; set; }
        public string Architecture { get; set; }
    }
}
