using System.Collections.Generic;

namespace HomeAutio.Mqtt.Ecobee
{
    /// <summary>
    /// Internal thermostat status.
    /// </summary>
    internal class ThermostatStatus
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ThermostatStatus"/> class.
        /// </summary>
        public ThermostatStatus()
        {
            Sensors = new Dictionary<string, IDictionary<string, string>>();

            Status = new Dictionary<string, string>();

            EquipmentStatus = new Dictionary<string, string>
            {
                { "heatPump", "off" },
                { "heatPump2", "off" },
                { "heatPump3", "off" },
                { "compCool1", "off" },
                { "compCool2", "off" },
                { "auxHeat1", "off" },
                { "auxHeat2", "off" },
                { "auxHeat3", "off" },
                { "fan", "off" },
                { "humidifier", "off" },
                { "dehumidifier", "off" },
                { "ventilator", "off" },
                { "economizer", "off" },
                { "compHotWater", "off" },
                { "auxHotWater", "off" }
            };
        }

        /// <summary>
        /// Sensor statuses.
        /// </summary>
        public IDictionary<string, IDictionary<string, string>> Sensors { get; }

        /// <summary>
        /// Thermostat statuses.
        /// </summary>
        public IDictionary<string, string> Status { get; }

        /// <summary>
        /// Equipment statuses.
        /// </summary>
        public IDictionary<string, string> EquipmentStatus { get; }
    }
}
