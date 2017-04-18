using System.Collections.Generic;

namespace HomeAutio.Mqtt.Ecobee
{
    internal class ThermostatStatus
    {
        public ThermostatStatus()
        {
            Sensors = new Dictionary<string, IDictionary<string, string>>();

            Status = new Dictionary<string, string>();

            EquipmentStatus = new Dictionary<string, bool>
            {
                { "heatPump", false },
                { "heatPump2", false },
                { "heatPump3", false },
                { "compCool1", false },
                { "compCool2", false },
                { "auxHeat1", false },
                { "auxHeat2", false },
                { "auxHeat3", false },
                { "fan", false },
                { "humidifier", false },
                { "dehumidifier", false },
                { "ventilator", false },
                { "economizer", false },
                { "compHotWater", false },
                { "auxHotWater", false }
            };
        }

        public IDictionary<string, IDictionary<string, string>> Sensors { get; }
        public IDictionary<string, string> Status { get; }
        public IDictionary<string, bool> EquipmentStatus { get; }
    }
}
