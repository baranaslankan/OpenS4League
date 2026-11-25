namespace OpenS4L.Server.Game.Data
{
    public class MapInfo
    {
        /// <summary>
        /// The full map id from map.x7. The wire only carries a byte (see <see cref="WireId"/>),
        /// so ids above 255 (the "random" placeholders 993-999) are truncated by the client.
        /// </summary>
        public int Id { get; set; }
        public string Name { get; set; }
        public GameRule GameRule { get; set; }
        public int PlayerLimit { get; set; }
        public bool IsEnabled { get; set; }

        /// <summary>
        /// True for the client's "random map" placeholders (bginfo-random.ini). They are not
        /// loadable maps: a room created with one gets a random real map of the same mode.
        /// </summary>
        public bool IsRandom { get; set; }

        /// <summary>
        /// The map id as the client sends it in room messages (a byte)
        /// </summary>
        public byte WireId => (byte)Id;

        public override string ToString()
        {
            return $"{Name}({GameRule})";
        }
    }
}
