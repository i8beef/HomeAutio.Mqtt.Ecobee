namespace HomeAutio.Mqtt.Ecobee.Options
{
    /// <summary>
    /// Ecobee options.
    /// </summary>
    public class EcobeeOptions
    {
        /// <summary>
        /// Ecobee name.
        /// </summary>
        public string EcobeeName { get; set; } = "default";

        /// <summary>
        /// Ecobee app key.
        /// </summary>
        public string EcobeeAppKey { get; set; } = "blank";

        /// <summary>
        /// Refresh interval.
        /// </summary>
        public int RefreshInterval { get; set; } = 180;
    }
}
