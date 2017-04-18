using System.Collections.Generic;

namespace HomeAutio.Mqtt.Ecobee
{
    internal class ThermostatStatus
    {
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

        public IDictionary<string, IDictionary<string, string>> Sensors { get; }
        public IDictionary<string, string> Status { get; }
        public IDictionary<string, string> EquipmentStatus { get; }
    }
}
