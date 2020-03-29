using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using HomeAutio.Mqtt.Core;
using I8Beef.Ecobee;
using I8Beef.Ecobee.Protocol;
using I8Beef.Ecobee.Protocol.Functions;
using I8Beef.Ecobee.Protocol.Objects;
using I8Beef.Ecobee.Protocol.Thermostat;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;

namespace HomeAutio.Mqtt.Ecobee
{
    /// <summary>
    /// Ecobee MQTT Topshelf service class.
    /// </summary>
    public class EcobeeMqttService : ServiceBase
    {
        private readonly ILogger<EcobeeMqttService> _log;

        private readonly Client _client;

        private readonly IDictionary<string, RevisionStatus> _revisionStatusCache;
        private readonly IDictionary<string, ThermostatStatus> _thermostatStatus;
        private readonly int _refreshInterval;

        private bool _disposed = false;
        private System.Timers.Timer _refresh;

        /// <summary>
        /// Initializes a new instance of the <see cref="EcobeeMqttService"/> class.
        /// </summary>
        /// <param name="logger">Logging instance.</param>
        /// <param name="ecobeeClient">The Ecobee client.</param>
        /// <param name="ecobeeName">The target Ecobee name.</param>
        /// <param name="refreshInterval">The refresh interval.</param>
        /// <param name="brokerSettings">MQTT broker settings.</param>
        public EcobeeMqttService(
            ILogger<EcobeeMqttService> logger,
            Client ecobeeClient,
            string ecobeeName,
            int refreshInterval,
            BrokerSettings brokerSettings)
            : base(logger, brokerSettings, "ecobee/" + ecobeeName)
        {
            _log = logger;
            _refreshInterval = refreshInterval * 1000;
            SubscribedTopics.Add(TopicRoot + "/+/+/set");

            _client = ecobeeClient;
            _revisionStatusCache = new Dictionary<string, RevisionStatus>();
            _thermostatStatus = new Dictionary<string, ThermostatStatus>();
        }

        #region Service implementation

        /// <inheritdoc />
        protected override async Task StartServiceAsync(CancellationToken cancellationToken = default)
        {
            await GetInitialStatusAsync()
                .ConfigureAwait(false);

            // Enable refresh
            if (_refresh != null)
            {
                _refresh.Dispose();
            }

            _refresh = new System.Timers.Timer();
            _refresh.Elapsed += RefreshAsync;
            _refresh.Interval = _refreshInterval;
            _refresh.Start();
        }

        /// <inheritdoc />
        protected override Task StopServiceAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        #endregion

        #region MQTT Implementation

        /// <summary>
        /// Handles commands for the Harmony published to MQTT.
        /// </summary>
        /// <param name="e">Event args.</param>
        protected override async void Mqtt_MqttMsgPublishReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            var message = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
            _log.LogInformation("MQTT message received for topic " + e.ApplicationMessage.Topic + ": " + message);

            // Parse topic out
            var topicWithoutRoot = e.ApplicationMessage.Topic.Substring(TopicRoot.Length + 1);
            var thermostatId = topicWithoutRoot.Substring(0, topicWithoutRoot.IndexOf('/'));
            var thermostatTopic = topicWithoutRoot.Substring(topicWithoutRoot.IndexOf('/') + 1);

            // ONLY VALID OPTIONS
            // HvacMode (auto, auxHeatOnly, cool, heat, off) - hvacMode/set
            // Fan (on, off, auto) - desiredFanMode/set
            // SetHold (hold, resume) - hold/set
            // Desired Heat - desiredHeat/set
            // Desired Cool - desiredCool/set
            var request = new ThermostatUpdateRequest
            {
                Selection = new Selection { SelectionType = "thermostats", SelectionMatch = thermostatId }
            };

            _log.LogInformation($"Sending request to {request.Uri} with thermostat selection {thermostatId} for action {thermostatTopic}");

            switch (thermostatTopic)
            {
                case "hvacMode/set":
                    request.Thermostat = new { Settings = new { HvacMode = message } };
                    var hvacModeResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                        .ConfigureAwait(false);
                    _log.LogInformation($"{request.Uri} response: ({hvacModeResponse.Status.Code}) {hvacModeResponse.Status.Message}");
                    break;
                case "desiredFanMode/set":
                    request.Thermostat = new { Settings = new { Vent = message } };
                    var desiredFanModeResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                        .ConfigureAwait(false);
                    _log.LogInformation($"{request.Uri} response: ({desiredFanModeResponse.Status.Code}) {desiredFanModeResponse.Status.Message}");
                    break;
                case "hold/set":
                    if (message == "hold")
                    {
                        // TODO: Figure out how to pass both desired heat and cool values at same time
                        var holdFunc = new SetHoldFunction();
                        ((SetHoldParams)holdFunc.Params).HoldType = "indefinite";
                        ((SetHoldParams)holdFunc.Params).CoolHoldTemp = 0;
                        ((SetHoldParams)holdFunc.Params).HeatHoldTemp = 0;
                        request.Functions.Add(holdFunc);
                        ////_client.Post<ThermostatUpdateRequest, Response>(request);
                    }
                    else
                    {
                        var resumeFunc = new ResumeProgramFunction();
                        ((ResumeProgramParams)resumeFunc.Params).ResumeAll = true;
                        request.Functions.Add(resumeFunc);
                        await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                    }

                    break;
                case "desiredHeat/set":
                    if (int.TryParse(message, out int desiredHeatValue))
                    {
                        request.Thermostat = new { Runtime = new { DesiredHeat = desiredHeatValue * 10 } };
                        var desiredHeatResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({desiredHeatResponse.Status.Code}) {desiredHeatResponse.Status.Message}");
                    }

                    break;
                case "desiredCool/set":
                    if (int.TryParse(message, out int desiredCoolValue))
                    {
                        request.Thermostat = new { Runtime = new { DesiredCool = desiredCoolValue * 10 } };
                        var desiredCoolResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({desiredCoolResponse.Status.Code}) {desiredCoolResponse.Status.Message}");
                    }

                    break;
                default:
                    _log.LogWarning($"Unknown command topic {thermostatTopic} for thermostat {thermostatId}");
                    break;
            }
        }

        #endregion

        #region Ecobee implementation

        /// <summary>
        /// Heartbeat ping. Failure will result in the heartbeat being stopped, which will
        /// make any future calls throw an exception as the heartbeat indicator will be disabled.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private async void RefreshAsync(object sender, ElapsedEventArgs e)
        {
            // Get current revision status
            var summaryRequest = new ThermostatSummaryRequest { Selection = new Selection { SelectionType = "registered" } };
            var status = await _client.GetAsync<ThermostatSummaryRequest, ThermostatSummaryResponse>(summaryRequest);
            foreach (var thermostatRevision in status.RevisionList)
            {
                _log.LogInformation($"Got revision: {thermostatRevision}");
                var revisionStatus = new RevisionStatus(thermostatRevision);

                if (_revisionStatusCache[revisionStatus.ThermostatIdentifier].ThermostatRevision != revisionStatus.ThermostatRevision ||
                    _revisionStatusCache[revisionStatus.ThermostatIdentifier].RuntimeRevision != revisionStatus.RuntimeRevision)
                {
                    RefreshThermostatAsync(revisionStatus);
                }

                // Cache last run values
                _revisionStatusCache[revisionStatus.ThermostatIdentifier] = revisionStatus;
            }
        }

        /// <summary>
        /// Handles updating status for a thermostat and publishing changes.
        /// </summary>
        /// <param name="revisionStatus">Revision status that triggered the update.</param>
        private async void RefreshThermostatAsync(RevisionStatus revisionStatus)
        {
            // Build single thermostat request
            var thermostatRequest = new ThermostatRequest
            {
                Selection = new Selection
                {
                    SelectionType = "thermostats",
                    SelectionMatch = revisionStatus.ThermostatIdentifier,
                    IncludeEquipmentStatus = true,
                    IncludeSettings = true,
                    IncludeRuntime = true,
                    IncludeSensors = true
                }
            };

            var thermostatUpdate = await _client.GetAsync<ThermostatRequest, ThermostatResponse>(thermostatRequest)
                .ConfigureAwait(false);

            // Publish updates and cache new values
            var thermostat = thermostatUpdate.ThermostatList.FirstOrDefault();
            if (thermostat != null)
            {
                var thermostatStatus = new ThermostatStatus();

                // Equipment status
                foreach (var device in thermostat.EquipmentStatus.Split(',').Where(x => !string.IsNullOrEmpty(x)))
                {
                    thermostatStatus.EquipmentStatus[device] = "on";
                }

                // Status
                thermostatStatus.Status["hvacMode"] = thermostat.Settings.HvacMode;
                thermostatStatus.Status["humidifierMode"] = thermostat.Settings.HumidifierMode;
                thermostatStatus.Status["dehumidifierMode"] = thermostat.Settings.DehumidifierMode;
                thermostatStatus.Status["autoAway"] = thermostat.Settings.AutoAway ? "true" : "false";
                thermostatStatus.Status["vent"] = thermostat.Settings.Vent;
                thermostatStatus.Status["actualTemperature"] = (thermostat.Runtime.ActualTemperature / 10m).ToString();
                thermostatStatus.Status["actualHumidity"] = thermostat.Runtime.ActualHumidity.ToString();
                thermostatStatus.Status["desiredHeat"] = (thermostat.Runtime.DesiredHeat / 10m).ToString();
                thermostatStatus.Status["desiredCool"] = (thermostat.Runtime.DesiredCool / 10m).ToString();
                thermostatStatus.Status["desiredHumidity"] = thermostat.Runtime.DesiredHumidity.ToString();
                thermostatStatus.Status["desiredDehumidity"] = thermostat.Runtime.DesiredDehumidity.ToString();
                thermostatStatus.Status["desiredFanMode"] = thermostat.Runtime.DesiredFanMode;

                // Sensors
                if (thermostat.RemoteSensors != null && thermostat.RemoteSensors.Count > 0)
                {
                    foreach (var sensor in thermostat.RemoteSensors)
                    {
                        thermostatStatus.Sensors[sensor.Name] = sensor.Capability.ToDictionary(
                            s => s.Type,
                            s =>
                            {
                                // Convert temperature values to human readable
                                if (string.Equals(s.Type, "temperature", System.StringComparison.OrdinalIgnoreCase))
                                {
                                    if (decimal.TryParse(s.Value, out var decimalValue))
                                    {
                                        return (decimalValue / 10m).ToString();
                                    }
                                }

                                return s.Value;
                            });
                    }
                }

                if (_thermostatStatus.ContainsKey(revisionStatus.ThermostatIdentifier))
                {
                    // Publish updates
                    foreach (var device in thermostatStatus.EquipmentStatus)
                    {
                        if (device.Value != _thermostatStatus[revisionStatus.ThermostatIdentifier].EquipmentStatus[device.Key])
                        {
                            await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                .WithTopic($"{TopicRoot}/{revisionStatus.ThermostatIdentifier}/{device.Key}")
                                .WithPayload(device.Value.ToString())
                                .WithAtLeastOnceQoS()
                                .WithRetainFlag()
                                .Build())
                                .ConfigureAwait(false);
                        }
                    }

                    foreach (var status in thermostatStatus.Status)
                    {
                        if (status.Value != _thermostatStatus[revisionStatus.ThermostatIdentifier].Status[status.Key])
                        {
                            await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                .WithTopic($"{TopicRoot}/{revisionStatus.ThermostatIdentifier}/{status.Key}")
                                .WithPayload(status.Value)
                                .WithAtLeastOnceQoS()
                                .WithRetainFlag()
                                .Build())
                                .ConfigureAwait(false);
                        }
                    }

                    // Publish everything for new sensors, new capabilities, and changes in existing ability values
                    foreach (var sensor in thermostatStatus.Sensors)
                    {
                        foreach (var sensorCapability in sensor.Value)
                        {
                            if (!_thermostatStatus[revisionStatus.ThermostatIdentifier].Sensors.ContainsKey(sensor.Key))
                            {
                                await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                    .WithTopic($"{TopicRoot}/{revisionStatus.ThermostatIdentifier}/sensor/{sensor.Key}/{sensorCapability.Key}")
                                    .WithPayload(sensorCapability.Value)
                                    .WithAtLeastOnceQoS()
                                    .WithRetainFlag()
                                    .Build())
                                    .ConfigureAwait(false);
                            }
                            else if (!_thermostatStatus[revisionStatus.ThermostatIdentifier].Sensors[sensor.Key].ContainsKey(sensorCapability.Key))
                            {
                                await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                    .WithTopic($"{TopicRoot}/{revisionStatus.ThermostatIdentifier}/sensor/{sensor.Key}/{sensorCapability.Key}")
                                    .WithPayload(sensorCapability.Value)
                                    .WithAtLeastOnceQoS()
                                    .WithRetainFlag()
                                    .Build())
                                    .ConfigureAwait(false);
                            }
                            else if (sensorCapability.Value != _thermostatStatus[revisionStatus.ThermostatIdentifier].Sensors[sensor.Key][sensorCapability.Key])
                            {
                                await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                    .WithTopic($"{TopicRoot}/{revisionStatus.ThermostatIdentifier}/sensor/{sensor.Key}/{sensorCapability.Key}")
                                    .WithPayload(sensorCapability.Value)
                                    .WithAtLeastOnceQoS()
                                    .WithRetainFlag()
                                    .Build())
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                }
                else
                {
                    // Publish initial state
                    foreach (var device in thermostatStatus.EquipmentStatus)
                    {
                        await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                            .WithTopic($"{TopicRoot}/{revisionStatus.ThermostatIdentifier}/{device.Key}")
                            .WithPayload(device.Value.ToString())
                            .WithAtLeastOnceQoS()
                            .WithRetainFlag()
                            .Build())
                            .ConfigureAwait(false);
                    }

                    foreach (var status in thermostatStatus.Status)
                    {
                        await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                            .WithTopic($"{TopicRoot}/{revisionStatus.ThermostatIdentifier}/{status.Key}")
                            .WithPayload(status.Value)
                            .WithAtLeastOnceQoS()
                            .WithRetainFlag()
                            .Build())
                            .ConfigureAwait(false);
                    }

                    foreach (var sensor in thermostatStatus.Sensors)
                    {
                        foreach (var sensorCapability in sensor.Value)
                        {
                            await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                .WithTopic($"{TopicRoot}/{revisionStatus.ThermostatIdentifier}/sensor/{sensor.Key}/{sensorCapability.Key}")
                                .WithPayload(sensorCapability.Value)
                                .WithAtLeastOnceQoS()
                                .WithRetainFlag()
                                .Build())
                                .ConfigureAwait(false);
                        }
                    }
                }

                _thermostatStatus[revisionStatus.ThermostatIdentifier] = thermostatStatus;
            }
        }

        /// <summary>
        /// Get, cache, and publish initial states.
        /// </summary>
        /// <returns>An awaitable <see cref="Task"/>.</returns>
        private async Task GetInitialStatusAsync()
        {
            var summaryRequest = new ThermostatSummaryRequest { Selection = new Selection { SelectionType = "registered" } };
            var summary = await _client.GetAsync<ThermostatSummaryRequest, ThermostatSummaryResponse>(summaryRequest)
                .ConfigureAwait(false);

            // Set initial revision cache
            _revisionStatusCache.Clear();
            _thermostatStatus.Clear();
            foreach (var revision in summary.RevisionList)
            {
                _log.LogInformation($"Got revision: {revision}");
                var revisionStatus = new RevisionStatus(revision);

                RefreshThermostatAsync(revisionStatus);
                _revisionStatusCache.Add(revisionStatus.ThermostatIdentifier, revisionStatus);
            }
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Dispose implementation.
        /// </summary>
        /// <param name="disposing">Indicates if disposing.</param>
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_refresh != null)
                {
                    _refresh.Stop();
                    _refresh.Dispose();
                }
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        #endregion
    }
}
