﻿// <copyright file="BotConnector.cs" company="Microsoft Corporation">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace VoiceAssistantTest
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CognitiveServices.Speech;
    using Microsoft.CognitiveServices.Speech.Audio;
    using Microsoft.CognitiveServices.Speech.Dialog;
    using NAudio.Wave;
    using Newtonsoft.Json;
    using Activity = Microsoft.Bot.Schema.Activity;
    using IMessageActivity = Microsoft.Bot.Schema.IMessageActivity;

    /// <summary>
    /// Manages the Connection and Responses to and from the Bot using the DialogServiceConnector object.
    /// </summary>
    internal class BotConnector : IDisposable
    {
        private const int MaxSizeOfTtsAudioInBytes = 65536;
        private const int WavHeaderSizeInBytes = 44;
        private const int BytesToRead = 3200;
        private const uint ResponseCheckInterval = 100; // milliseconds
        private int responseCount;
        private int timeout;
        private string outputWAV;
        private DialogServiceConnector connector = null;
        private PushAudioInputStream pushAudioInputStream = null;
        private AppSettings appsettings;
        private string baseFileName;
        private string dialogID;
        private int turnID;
        private bool audioDownloading = false;
        private int indexActivityWithAudio = 0;
        private List<Activity> ignoreActivitiesList;
        private Stopwatch stopWatch;
        private string expectedRecognition;
        private string expectedLatency;

        /// <summary>
        /// Gets or sets TTS audio duration.
        /// </summary>
        public int DurationInMs { get; set; }

        /// <summary>
        /// Gets or sets recognized text of the speech input.
        /// </summary>
        public string RecognizedText { get; set; }

        private Queue<BotReply> ActivityQueue { get; set; }

        /// <summary>
        /// Initializes the connection to the Bot.
        /// </summary>
        /// <param name="settings">Application settings object, built from the input JSON file supplied as run-time argument.</param>
        public void InitConnector(AppSettings settings)
        {
            DialogServiceConfig config;
            this.ActivityQueue = new Queue<BotReply>();
            this.stopWatch = new Stopwatch();
            this.appsettings = settings;

            if (!string.IsNullOrWhiteSpace(this.appsettings.CustomCommandsAppId))
            {
                // NOTE: Custom commands is a preview Azure Service.
                // Set the custom commands configuration object based on three items:
                // - The Custom commands application ID
                // - Cognitive services speech subscription key.
                // - The Azure region of the subscription key(e.g. "westus").
                config = CustomCommandsConfig.FromSubscription(this.appsettings.CustomCommandsAppId, this.appsettings.SubscriptionKey, this.appsettings.Region);
            }
            else
            {
                // Set the bot framework configuration object based on two items:
                // - Cognitive services speech subscription key. It is needed for billing and is tied to the bot registration.
                // - The Azure region of the subscription key(e.g. "westus").
                config = BotFrameworkConfig.FromSubscription(this.appsettings.SubscriptionKey, this.appsettings.Region);
            }

            if (this.appsettings.SpeechSDKLogEnabled)
            {
                // Speech SDK has verbose logging to local file, which may be useful when reporting issues.
                config.SetProperty(PropertyId.Speech_LogFilename, $"{this.appsettings.OutputFolder}SpeechSDKLog-{DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.CurrentCulture)}.txt");
            }

            if (!string.IsNullOrWhiteSpace(this.appsettings.SRLanguage))
            {
                // Set the speech recognition language. If not set, the default is "en-us".
                config.Language = this.appsettings.SRLanguage;
            }

            if (!string.IsNullOrWhiteSpace(this.appsettings.CustomSREndpointId))
            {
                // Set your custom speech end-point id here, as given to you by the speech portal https://speech.microsoft.com/portal.
                // Otherwise the standard speech end-point will be used.
                config.SetServiceProperty("cid", this.appsettings.CustomSREndpointId, ServicePropertyChannel.UriQueryParameter);

                // Custom Speech does not support cloud Keyword Verification at the moment. If this is not done, there will be an error
                // from the service and connection will close. Remove line below when supported.
                config.SetProperty("KeywordConfig_EnableKeywordVerification", "false");
            }

            if (!string.IsNullOrWhiteSpace(this.appsettings.CustomVoiceDeploymentIds))
            {
                // Set one or more IDs associated with the custom TTS voice your bot will use.
                // The format of the string is one or more GUIDs separated by comma (no spaces). You get these GUIDs from
                // your custom TTS on the speech portal https://speech.microsoft.com/portal.
                config.SetProperty(PropertyId.Conversation_Custom_Voice_Deployment_Ids, this.appsettings.CustomVoiceDeploymentIds);
            }

            if (!string.IsNullOrWhiteSpace(this.appsettings.Timeout))
            {
                // TODO: Verify if this string is a valid integer - timeout can only be a non-negative integer representing time in milliseconds.
                this.timeout = int.Parse(this.appsettings.Timeout, CultureInfo.CurrentCulture);
            }

            if (this.connector != null)
            {
                // Then dispose the object
                this.connector.Dispose();
                this.connector = null;
            }

            this.pushAudioInputStream = AudioInputStream.CreatePushStream();
            this.connector = new DialogServiceConnector(config, AudioConfig.FromStreamInput(this.pushAudioInputStream));
            this.AttachHandlers();
        }

        /// <summary>
        /// Connects to the Bot using the DialogServiceConnector object.
        /// </summary>
        /// <returns>Connection to Bot.</returns>
        public async Task Connect()
        {
            if (this.connector != null)
            {
                await this.connector.ConnectAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Disconnects from the Bot.
        /// </summary>
        /// <returns>Disconnection.</returns>
        public async Task Disconnect()
        {
            if (this.connector != null)
            {
                this.DetachHandlers();
                await this.connector.DisconnectAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sends a message activity to the bot after wrapping a message in an Activity object.
        /// </summary>
        /// <param name="message">Utterance text in each turn.</param>
        /// <returns>Activity.</returns>
        public async Task<BotConnector> Send(string message)
        {
            IMessageActivity bfActivity = Activity.CreateMessageActivity();
            bfActivity.Text = message;
            string jsonConnectorActivity = JsonConvert.SerializeObject(bfActivity);

            return await this.SendActivity(jsonConnectorActivity).ConfigureAwait(false);
        }

        /// <summary>
        /// Send an audio WAV file to the Bot using ListenOnceAsync.
        /// </summary>
        /// <returns>Activity.</returns>
        public async Task<BotConnector> SendAudio(string wavFile)
        {
            int readBytes;

            lock (this.ActivityQueue)
            {
                this.ActivityQueue.Clear();
                this.indexActivityWithAudio = 0;
            }

            byte[] dataBuffer = new byte[MaxSizeOfTtsAudioInBytes];
            WaveFileReader waveFileReader = new WaveFileReader(this.appsettings.InputFolder + wavFile);

            // Reading header bytes
            int headerBytes = waveFileReader.Read(dataBuffer, 0, WavHeaderSizeInBytes);

            while ((readBytes = waveFileReader.Read(dataBuffer, 0, BytesToRead)) > 0)
            {
                this.pushAudioInputStream.Write(dataBuffer, readBytes);
            }

            waveFileReader.Dispose();

            Trace.TraceInformation($"[{DateTime.Now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}] Call ListenOnceAsync() and wait");

            // Don't wait for this task to finish. It may take a while, even after the "Recognized" event is received. This is a known
            // issue in Speech SDK and should be fixed in a future versions.
            // this.connector.ListenOnceAsync();
            await this.connector.ListenOnceAsync().ConfigureAwait(false);
            Trace.TraceInformation($"[{DateTime.Now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}] ListenOnceAsync() task completed");
            return this;
        }

        /// <summary>
        /// Sends an Activity to the Bot using SendActivityAsync.
        /// </summary>
        /// <param name="activity">Activity in each turn.</param>
        /// <returns>Activity.</returns>
        public async Task<BotConnector> SendActivity(string activity)
        {
            this.stopWatch.Restart();

            lock (this.ActivityQueue)
            {
                this.ActivityQueue.Clear();
                this.indexActivityWithAudio = 0;
            }

            string result = await this.connector.SendActivityAsync(activity).ConfigureAwait(false);
            Trace.TraceInformation($"[{DateTime.Now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}] Activity sent to channel. InteractionID {result}");
            return this;
        }

        /// <summary>
        /// Collects the expected number of bot reply activities and sorts them by timestamp.
        /// Filters the received activities and removes activities that are specified in the ignoreActivitiesList.
        /// The expected number of responses is set in the responseCount variable.
        /// </summary>
        /// <returns>List of time-sorted and filtered bot-reply Activities.</returns>
        public List<BotReply> WaitAndProcessBotReplies()
        {
            CancellationTokenSource source = new CancellationTokenSource();
            CancellationToken token = source.Token;
            List<BotReply> activities = new List<BotReply>();

            var getExpectedResponses = Task.Run(
                () =>
                {
                    // Make this configurable per interaction as an input row
                    while (activities.Count < this.responseCount)
                    {
                        Thread.Sleep((int)ResponseCheckInterval);

                        lock (this.ActivityQueue)
                        {
                            if (this.ActivityQueue.TryDequeue(out var item))
                            {
                                if (!this.IgnoreActivity((Activity)item.Activity))
                                {
                                    activities.Add((BotReply)item);
                                }
                            }
                        }
                    }

                    // Wait until TTS audio finishes downloading (if there is one), so its duration can be calculated. TTS audio duration
                    // may be part of test pass/fail validation.
                    while (this.audioDownloading)
                    {
                        Thread.Sleep((int)ResponseCheckInterval);
                    }
                }, cancellationToken: token);

            if (Task.WhenAny(getExpectedResponses, Task.Delay((int)this.timeout)).Result == getExpectedResponses)
            {
                Trace.TraceInformation($"Task status {getExpectedResponses.Status}. Received {activities.Count} activities, as expected (configured to wait for {this.responseCount}):");
            }
            else
            {
                Trace.TraceInformation(
                        $"[{DateTime.Now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}] Timed out waiting for expected replies. Received {activities.Count} activities (configured to wait for {this.responseCount}):");
                source.Cancel();
            }

            for (int count = 0; count < activities.Count; count++)
            {
                Trace.TraceInformation($"[{count}]: Latency {activities[count].Latency} msec");
            }

            activities.Sort((a, b) =>
            {
                return DateTimeOffset.Compare(a.Activity.Timestamp ?? default, b.Activity.Timestamp ?? default);
            });

            source.Dispose();
            return activities;
        }

        /// <summary>
        /// Disposes the DialogServiceConnector object.
        /// </summary>
        public void Dispose()
        {
            this.connector.Dispose();
            this.pushAudioInputStream.Dispose();
        }

        /// <summary>
        /// Compares a given activity to the list of activities specified by IgnoringActivities list in the test configuration.
        /// </summary>
        /// <param name="activity">An activity that the client received from the bot.</param>
        /// <returns>true if the activity matches one of the activities in the list. Otherwise returns false.</returns>
        public bool IgnoreActivity(Activity activity)
        {
            bool ignore = false;

            if (this.ignoreActivitiesList != null)
            {
                foreach (Activity activityToIgnore in this.ignoreActivitiesList)
                {
                    if (DialogResultUtility.ActivitiesMatch(activityToIgnore, activity))
                    {
                        Trace.TraceInformation($"Bot-reply activity matched IgnoringActivities[{this.ignoreActivitiesList.IndexOf(activityToIgnore)}]. Ignore it.");
                        ignore = true;
                        break;
                    }
                }
            }

            return ignore;
        }

        /// <summary>
        /// Obtains and sets the utterance, dialogId, and turnId, and responseCount for each Dialog and Turn
        /// Obtains ad sets list of ignoringActivities and timeout value defined in the Config for each InputFile.
        /// </summary>
        /// <param name="utterance">The text value of the Utterance field in the input test file.</param>
        /// <param name="fileName">The name of the input test file.</param>
        /// <param name="dialogID">The value of the DialogID in the input test file.</param>
        /// <param name="turnID">The value of the TurnID in the input test file.</param>
        /// <param name="responseCount">Number of bot activity responses expected for this turn (after filtering out activities marked for ignoring).</param>
        /// <param name="ignoringActivities">List of Activities to Ignore for the bot as defined in the Config File.</param>
        /// <param name="expectedLatency">Expected Latency for the bot as defined in the Config File.</param>
        public void SetInputValues(string utterance, string fileName, string dialogID, int turnID, int responseCount, List<Activity> ignoringActivities, string expectedLatency)
        {
            this.expectedRecognition = utterance;
            this.baseFileName = Path.GetFileNameWithoutExtension(fileName);
            this.dialogID = dialogID;
            this.turnID = turnID;
            this.responseCount = responseCount;
            this.ignoreActivitiesList = ignoringActivities;
            this.expectedLatency = expectedLatency;
        }

        /// <summary>
        /// Write header to a WAV file.
        /// </summary>
        /// <param name="fs"> Filestream.</param>
        private static void WriteWavHeader(FileStream fs)
        {
            ushort channels = 1;
            int sampleRate = 16000;
            ushort bytesPerSample = 2;

            fs.Position = 0;

            // RIFF header.
            // Chunk ID.
            fs.Write(Encoding.ASCII.GetBytes("RIFF"), 0, 4);

            // Chunk size.
            fs.Write(BitConverter.GetBytes((int)fs.Length - 8), 0, 4);

            // Format.
            fs.Write(Encoding.ASCII.GetBytes("WAVE"), 0, 4);

            // Sub-chunk 1.
            // Sub-chunk 1 ID.
            fs.Write(Encoding.ASCII.GetBytes("fmt "), 0, 4);

            // Sub-chunk 1 size.
            fs.Write(BitConverter.GetBytes(16), 0, 4);

            // Audio format (floating point (3) or PCM (1)). Any other format indicates compression.
            fs.Write(BitConverter.GetBytes((ushort)1), 0, 2);

            // Channels.
            fs.Write(BitConverter.GetBytes(channels), 0, 2);

            // Sample rate.
            fs.Write(BitConverter.GetBytes(sampleRate), 0, 4);

            // Bytes rate.
            fs.Write(BitConverter.GetBytes(sampleRate * channels * bytesPerSample), 0, 4);

            // Block align.
            fs.Write(BitConverter.GetBytes((ushort)channels * bytesPerSample), 0, 2);

            // Bits per sample.
            fs.Write(BitConverter.GetBytes((ushort)(bytesPerSample * 8)), 0, 2);

            // Sub-chunk 2.
            // Sub-chunk 2 ID.
            fs.Write(Encoding.ASCII.GetBytes("data"), 0, 4);

            // Sub-chunk 2 size.
            fs.Write(BitConverter.GetBytes((int)(fs.Length - 44)), 0, 4);
        }

        private void AttachHandlers()
        {
            if (this.connector != null)
            {
                this.connector.ActivityReceived += this.SpeechBotConnector_ActivityReceived;
                this.connector.Recognized += this.SpeechBotConnector_Recognized;
                this.connector.Canceled += this.SpeechBotConnector_Canceled;
                this.connector.SessionStarted += this.SpeechBotConnector_SessionStarted;
                this.connector.SessionStopped += this.SpeechBotConnector_SessionStopped;
            }
        }

        private void DetachHandlers()
        {
            if (this.connector != null)
            {
                this.connector.ActivityReceived -= this.SpeechBotConnector_ActivityReceived;
                this.connector.Canceled -= this.SpeechBotConnector_Canceled;
            }
        }

        private void SpeechBotConnector_Recognized(object sender, SpeechRecognitionEventArgs e)
        {
            this.RecognizedText = e.Result.Text;

            Trace.TraceInformation($"[{DateTime.Now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}] Recognized event received. SessionId = {e.SessionId}");

            this.stopWatch.Restart();
        }

        private void SpeechBotConnector_ActivityReceived(object sender, ActivityReceivedEventArgs e)
        {
            var json = e.Activity;
            var activity = JsonConvert.DeserializeObject<Activity>(json);

            this.stopWatch.Stop();

            lock (this.ActivityQueue)
            {
                this.ActivityQueue.Enqueue(new BotReply(activity, (int)this.stopWatch.ElapsedMilliseconds));
            }

            if (e.HasAudio)
            {
                this.audioDownloading = true;
                this.WriteAudioToWAVfile(e.Audio, this.baseFileName, this.dialogID, this.turnID, this.indexActivityWithAudio);
                this.indexActivityWithAudio++;
            }

            this.stopWatch.Restart();
        }

        private void SpeechBotConnector_Canceled(object sender, SpeechRecognitionCanceledEventArgs e)
        {
            if (e.Reason == CancellationReason.Error)
            {
                Trace.TraceInformation($"[{DateTime.Now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}] Canceled event received due to an error. {e.ErrorCode} - {e.ErrorDetails}.");
            }
            else if (e.Reason == CancellationReason.EndOfStream)
            {
                Trace.TraceInformation($"[{DateTime.Now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}] Canceled event received due to end of stream.");
            }
        }

        private void SpeechBotConnector_SessionStarted(object sender, SessionEventArgs e)
        {
            Trace.TraceInformation($"[{DateTime.Now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}] Session Started event received. SessionId = {e.SessionId}");
        }

        private void SpeechBotConnector_SessionStopped(object sender, SessionEventArgs e)
        {
            Trace.TraceInformation($"[{DateTime.Now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}] Session Stopped event received. SessionId = {e.SessionId}");
        }

        /// <summary>
        /// Write TTS Audio to WAV file.
        /// </summary>
        /// <param name="audio"> TTS Audio.</param>
        /// <param name="baseFileName"> File name where this test is specified. </param>
        /// <param name="dialogID">The value of the DialogID in the input test file.</param>
        /// <param name="turnID">The value of the TurnID in the input test file.</param>
        /// <param name="indexActivityWithAudio">Index value of the current TTS response.</param>
        private void WriteAudioToWAVfile(PullAudioOutputStream audio, string baseFileName, string dialogID, int turnID, int indexActivityWithAudio)
        {
            FileStream fs = null;
            string testFileOutputFolder = Path.Combine(this.appsettings.OutputFolder, baseFileName + "Output");
            string wAVFolderPath = Path.Combine(testFileOutputFolder, ProgramConstants.WAVFileFolderName);

            if (indexActivityWithAudio == 0)
            {
                // First TTS WAV file to be written, create the WAV File Folder
                Directory.CreateDirectory(wAVFolderPath);
            }

            this.outputWAV = Path.Combine(wAVFolderPath, baseFileName + "-BotResponse-" + dialogID + "-" + turnID + "-" + indexActivityWithAudio + ".WAV");
            byte[] buff = new byte[MaxSizeOfTtsAudioInBytes];
            uint bytesReadtofile;

            try
            {
                fs = File.Create(this.outputWAV);
                fs.Write(new byte[WavHeaderSizeInBytes]);
                while ((bytesReadtofile = audio.Read(buff)) > 0)
                {
                    fs.Write(buff, 0, (int)bytesReadtofile);
                }

                WriteWavHeader(fs);
            }
            catch (Exception e)
            {
                Trace.TraceError(e.ToString());
            }
            finally
            {
                fs.Close();
            }

            WaveFileReader waveFileReader = new WaveFileReader(this.outputWAV);
            this.DurationInMs = (int)waveFileReader.TotalTime.TotalMilliseconds;
            waveFileReader.Dispose();
            this.audioDownloading = false;
        }
    }
}
