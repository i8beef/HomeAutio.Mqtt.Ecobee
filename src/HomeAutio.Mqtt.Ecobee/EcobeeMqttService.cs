using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using HomeAutio.Mqtt.Core;
using HomeAutio.Mqtt.Core.Utilities;
using I8Beef.Ecobee;
using I8Beef.Ecobee.Protocol;
using I8Beef.Ecobee.Protocol.Functions;
using I8Beef.Ecobee.Protocol.Objects;
using I8Beef.Ecobee.Protocol.Thermostat;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using Newtonsoft.Json;

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
            _refresh.Elapsed += async (sender, e) => await RefreshAsync();
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
            // SetHold ({ hold object }) - hold/set
            // Desired Heat - desiredHeat/set
            // Desired Cool - desiredCool/set
            var request = new ThermostatUpdateRequest
            {
                Selection = new Selection
                {
                    SelectionType = "thermostats",
                    SelectionMatch = thermostatId
                }
            };

            _log.LogInformation($"Sending request to {request.Uri} with thermostat selection {thermostatId} for action {thermostatTopic}");

            switch (thermostatTopic)
            {
                case "desiredCool/set":
                    if (int.TryParse(message, out int desiredCoolValue) && int.TryParse(_thermostatStatus[thermostatId].Status["desiredHeat"], out int currentDesiredHeatValue))
                    {
                        request.Functions = new List<Function>
                        {
                            new SetHoldFunction
                            {
                                Params = new SetHoldParams
                                {
                                    HoldType = "nextTransition",
                                    CoolHoldTemp = desiredCoolValue * 10,
                                    HeatHoldTemp = currentDesiredHeatValue * 10
                                }
                            }
                        };
                        var desiredCoolResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({desiredCoolResponse.Status.Code}) {desiredCoolResponse.Status.Message}");

                        // Publish updates and cache new values
                        await GetInitialStatusAsync();
                    }

                    break;
                case "desiredFanMode/set":
                    if (message == "auto" || message == "off" || message == "on")
                    {
                        request.Functions = new List<Function>
                        {
                            new SetHoldFunction
                            {
                                Params = new SetHoldParams
                                {
                                    HoldType = "nextTransition",
                                    Fan = message
                                }
                            }
                        };

                        var desiredFanModeResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({desiredFanModeResponse.Status.Code}) {desiredFanModeResponse.Status.Message}");

                        // Publish updates and cache new values
                        await GetInitialStatusAsync();
                    }

                    break;
                case "desiredHeat/set":
                    if (int.TryParse(message, out int desiredHeatValue) && int.TryParse(_thermostatStatus[thermostatId].Status["desiredCool"], out int currentDesiredCoolValue))
                    {
                        request.Functions = new List<Function>
                        {
                            new SetHoldFunction
                            {
                                Params = new SetHoldParams
                                {
                                    HoldType = "nextTransition",
                                    CoolHoldTemp = currentDesiredCoolValue * 10,
                                    HeatHoldTemp = desiredHeatValue * 10
                                }
                            }
                        };

                        var desiredHeatResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({desiredHeatResponse.Status.Code}) {desiredHeatResponse.Status.Message}");

                        // Publish updates and cache new values
                        await GetInitialStatusAsync();
                    }

                    break;
                case "hold/set":
                    try
                    {
                        if (message.Contains("resumeProgram"))
                        {
                            var resumeFunction = JsonSerializer<ResumeProgramFunction>.Deserialize(message);
                            request.Functions = new List<Function>
                            {
                                resumeFunction
                            };
                        }
                        else
                        {
                            var holdFunction = JsonSerializer<SetHoldFunction>.Deserialize(message);
                            request.Functions = new List<Function>
                            {
                                holdFunction
                            };
                        }

                        var setHoldResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({setHoldResponse.Status.Code}) {setHoldResponse.Status.Message}");

                        // Publish updates and cache new values
                        await GetInitialStatusAsync();
                    }
                    catch (JsonException ex)
                    {
                        _log.LogWarning(ex, $"Could not deserialize payload: {message}");
                    }

                    break;
                case "hvacMode/set":
                    if (message == "auto" || message == "auxHeatOnly" || message == "cool" || message == "heat" || message == "off")
                    {
                        request.Thermostat = new { Settings = new { HvacMode = message } };
                        var hvacModeResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({hvacModeResponse.Status.Code}) {hvacModeResponse.Status.Message}");

                        // Publish updates and cache new values
                        await GetInitialStatusAsync();
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
        /// <returns>>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task RefreshAsync()
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
                    await RefreshThermostatAsync(revisionStatus);
                }

                // Cache last run values
                _revisionStatusCache[revisionStatus.ThermostatIdentifier] = revisionStatus;
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

                await RefreshThermostatAsync(revisionStatus);
                _revisionStatusCache.Add(revisionStatus.ThermostatIdentifier, revisionStatus);
            }
        }

        /// <summary>
        /// Handles updating status for a thermostat and publishing changes.
        /// </summary>
        /// <param name="revisionStatus">Revision status that triggered the update.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task RefreshThermostatAsync(RevisionStatus revisionStatus)
        {
            // Build single thermostat request
            var thermostatRequest = new ThermostatRequest
            {
                Selection = new Selection
                {
                    SelectionType = "thermostats",
                    SelectionMatch = revisionStatus.ThermostatIdentifier,
                    IncludeEquipmentStatus = true,
                    IncludeEvents = true,
                    IncludeRuntime = true,
                    IncludeSensors = true,
                    IncludeSettings = true,
                    IncludeWeather = true
                }
            };

            var thermostatUpdate = await _client.GetAsync<ThermostatRequest, ThermostatResponse>(thermostatRequest)
                .ConfigureAwait(false);

            // Publish updates and cache new values
            await UpdateState(thermostatUpdate?.ThermostatList?.FirstOrDefault());
        }

        /// <summary>
        /// Update state and publish new statuses.
        /// </summary>
        /// <param name="thermostat">Thermostat status.</param>
        /// <returns>>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task UpdateState(Thermostat thermostat)
        {
            if (thermostat == null)
            {
                return;
            }

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

            // Weather forcasts
            var forecast = thermostat.Weather.Forecasts?.FirstOrDefault();
            if (forecast != null)
            {
                thermostatStatus.Status["weatherDewPoint"] = (forecast.Dewpoint / 10m).ToString();
                thermostatStatus.Status["weatherPrecipitationChance"] = forecast.Pop.ToString();
                thermostatStatus.Status["weatherPressure"] = (forecast.Pressure / 100m).ToString();
                thermostatStatus.Status["weatherRelativeHumidity"] = forecast.RelativeHumidity.ToString();
                thermostatStatus.Status["weatherTemperature"] = (forecast.Temperature / 10m).ToString();
                thermostatStatus.Status["weatherTempLow"] = (forecast.TempLow / 10m).ToString();
                thermostatStatus.Status["weatherTempHigh"] = (forecast.TempHigh / 10m).ToString();
                thermostatStatus.Status["weatherVisibility"] = forecast.Visibility.ToString();
                thermostatStatus.Status["weatherWindBearing"] = forecast.WindBearing.ToString();
                thermostatStatus.Status["weatherWindDirection"] = forecast.WindDirection.ToString();
                thermostatStatus.Status["weatherWindGust"] = forecast.WindGust.ToString();
                thermostatStatus.Status["weatherWindSpeed"] = forecast.WindSpeed.ToString();
            }

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

            // Hold
            var holdEvent = thermostat.Events.FirstOrDefault(x => x.Type == "hold");
            if (holdEvent != null && holdEvent.Running)
            {
                thermostatStatus.ActiveHold["running"] = holdEvent.Running.ToString();
                thermostatStatus.ActiveHold["startTime"] = DateTime.Parse($"{holdEvent.StartDate} {holdEvent.StartTime}").ToString();
                thermostatStatus.ActiveHold["endTime"] = DateTime.Parse($"{holdEvent.EndDate} {holdEvent.EndTime}").ToString();
                thermostatStatus.ActiveHold["coldHoldTemp"] = (holdEvent.CoolHoldTemp / 10m).ToString();
                thermostatStatus.ActiveHold["heatHoldTemp"] = (holdEvent.HeatHoldTemp / 10m).ToString();
                thermostatStatus.ActiveHold["fan"] = holdEvent.Fan;
                thermostatStatus.ActiveHold["fanMinOnTime"] = holdEvent.FanMinOnTime.ToString();
                thermostatStatus.ActiveHold["vent"] = holdEvent.Vent;
                thermostatStatus.ActiveHold["ventilatorMinOnTime"] = holdEvent.VentilatorMinOnTime.ToString();
            }

            if (_thermostatStatus.ContainsKey(thermostat.Identifier))
            {
                // Publish updates
                foreach (var device in thermostatStatus.EquipmentStatus)
                {
                    if (device.Value != _thermostatStatus[thermostat.Identifier].EquipmentStatus[device.Key])
                    {
                        await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                            .WithTopic($"{TopicRoot}/{thermostat.Identifier}/{device.Key}")
                            .WithPayload(device.Value.ToString())
                            .WithAtLeastOnceQoS()
                            .WithRetainFlag()
                            .Build())
                            .ConfigureAwait(false);
                    }
                }

                foreach (var status in thermostatStatus.Status)
                {
                    if (status.Value != _thermostatStatus[thermostat.Identifier].Status[status.Key])
                    {
                        await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                            .WithTopic($"{TopicRoot}/{thermostat.Identifier}/{status.Key}")
                            .WithPayload(status.Value)
                            .WithAtLeastOnceQoS()
                            .WithRetainFlag()
                            .Build())
                            .ConfigureAwait(false);
                    }
                }

                // Hold status
                foreach (var holdStatus in thermostatStatus.ActiveHold)
                {
                    if (holdStatus.Value != _thermostatStatus[thermostat.Identifier].ActiveHold[holdStatus.Key])
                    {
                        await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                            .WithTopic($"{TopicRoot}/{thermostat.Identifier}/hold/{holdStatus.Key}")
                            .WithPayload(holdStatus.Value)
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
                        if (!_thermostatStatus[thermostat.Identifier].Sensors.ContainsKey(sensor.Key))
                        {
                            await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                .WithTopic($"{TopicRoot}/{thermostat.Identifier}/sensor/{sensor.Key.Sluggify()}/{sensorCapability.Key}")
                                .WithPayload(sensorCapability.Value)
                                .WithAtLeastOnceQoS()
                                .WithRetainFlag()
                                .Build())
                                .ConfigureAwait(false);
                        }
                        else if (!_thermostatStatus[thermostat.Identifier].Sensors[sensor.Key].ContainsKey(sensorCapability.Key))
                        {
                            await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                .WithTopic($"{TopicRoot}/{thermostat.Identifier}/sensor/{sensor.Key.Sluggify()}/{sensorCapability.Key}")
                                .WithPayload(sensorCapability.Value)
                                .WithAtLeastOnceQoS()
                                .WithRetainFlag()
                                .Build())
                                .ConfigureAwait(false);
                        }
                        else if (sensorCapability.Value != _thermostatStatus[thermostat.Identifier].Sensors[sensor.Key][sensorCapability.Key])
                        {
                            await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                .WithTopic($"{TopicRoot}/{thermostat.Identifier}/sensor/{sensor.Key.Sluggify()}/{sensorCapability.Key}")
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
                        .WithTopic($"{TopicRoot}/{thermostat.Identifier}/{device.Key}")
                        .WithPayload(device.Value.ToString())
                        .WithAtLeastOnceQoS()
                        .WithRetainFlag()
                        .Build())
                        .ConfigureAwait(false);
                }

                foreach (var status in thermostatStatus.Status)
                {
                    await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                        .WithTopic($"{TopicRoot}/{thermostat.Identifier}/{status.Key}")
                        .WithPayload(status.Value)
                        .WithAtLeastOnceQoS()
                        .WithRetainFlag()
                        .Build())
                        .ConfigureAwait(false);
                }

                // Hold status
                foreach (var holdStatus in thermostatStatus.ActiveHold)
                {
                    if (holdStatus.Value != _thermostatStatus[thermostat.Identifier].ActiveHold[holdStatus.Key])
                    {
                        await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                            .WithTopic($"{TopicRoot}/{thermostat.Identifier}/hold/{holdStatus.Key}")
                            .WithPayload(holdStatus.Value)
                            .WithAtLeastOnceQoS()
                            .WithRetainFlag()
                            .Build())
                            .ConfigureAwait(false);
                    }
                }

                foreach (var sensor in thermostatStatus.Sensors)
                {
                    foreach (var sensorCapability in sensor.Value)
                    {
                        await MqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                            .WithTopic($"{TopicRoot}/{thermostat.Identifier}/sensor/{sensor.Key.Sluggify()}/{sensorCapability.Key}")
                            .WithPayload(sensorCapability.Value)
                            .WithAtLeastOnceQoS()
                            .WithRetainFlag()
                            .Build())
                            .ConfigureAwait(false);
                    }
                }
            }

            _thermostatStatus[thermostat.Identifier] = thermostatStatus;
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
