namespace HomeAutio.Mqtt.Ecobee
{
    internal class RevisionStatus
    {
        public RevisionStatus(string csv)
        {
            var parts = csv.Split(':');

            ThermostatIdentifier = parts[0];
            ThermostatName = parts[1];
            Connected = parts[2] == "true";

            if (parts.Length >= 3)
                ThermostatRevision = parts[3];

            if (parts.Length >= 4)
                AlertsRevision = parts[4];

            if (parts.Length >= 5)
                RuntimeRevision = parts[5];

            if (parts.Length >= 6)
                IntervalRevision = parts[6];
        }

        public RevisionStatus(string thermostatIdentifier, string thermostatName, bool connected, string thermostatRevision, string alertsRevision, string runtimeRevision, string intervalRevision)
        {
            ThermostatIdentifier = thermostatIdentifier;
            ThermostatName = thermostatName;
            Connected = connected;
            ThermostatRevision = thermostatRevision;
            AlertsRevision = alertsRevision;
            RuntimeRevision = runtimeRevision;
            IntervalRevision = intervalRevision;
        }

        /// <summary>
        /// The thermostat identifier.
        /// </summary>
        public string ThermostatIdentifier { get; set; }

        /// <summary>
        /// The thermostat name, otherwise an empty field if one is not set.
        /// </summary>
        public string ThermostatName { get; set; }

        /// <summary>
        /// Whether the thermostat is currently connected to the ecobee servers.
        /// </summary>
        public bool Connected { get; set; }

        /// <summary>
        /// Current thermostat revision. This revision is incremented whenever the thermostat program, 
        /// hvac mode, settings or configuration change. Changes to the following objects will update 
        /// the thermostat revision: Settings, Program, Event, Device.
        /// </summary>
        public string ThermostatRevision { get; set; }

        /// <summary>
        /// Current revision of the thermostat alerts.This revision is incremented whenever a new Alert 
        /// is issued or an Alert is modified(acknowledged or deferred).
        /// </summary>
        public string AlertsRevision { get; set; }

        /// <summary>
        /// The current revision of the thermostat runtime settings. This revision is incremented whenever 
        /// the thermostat transmits a new status message, or updates the equipment state or Remote Sensor 
        /// readings. The shortest interval this revision may change is 3 minutes.
        /// </summary>
        public string RuntimeRevision { get; set; }

        /// <summary>
        /// The current revision of the thermostat interval runtime settings. This revision is incremented 
        /// whenever the thermostat transmits a new status message in the form of a Runtime object. The 
        /// thermostat updates this on a 15 minute interval.
        /// </summary>
        public string IntervalRevision { get; set; }
    }
}
