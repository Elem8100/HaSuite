using System;
using NAudio.CoreAudioApi;
using System.Threading;
using System.Runtime.InteropServices;

namespace NAudio.Wave
{
    /// <summary>
    /// Support for playback using Wasapi
    /// </summary>
    public class WasapiOut : IWavePlayer, IWavePosition
    {
        private AudioClient audioClient;
        private readonly MMDevice mmDevice;
        private readonly AudioClientShareMode shareMode;
        private AudioRenderClient renderClient;
        private IWaveProvider sourceProvider;
        private int latencyMilliseconds;
        private int bufferFrameCount;
        private int bytesPerFrame;
        private readonly bool isUsingEventSync;
        private EventWaitHandle frameEventWaitHandle;
        private byte[] readBuffer;
        private volatile PlaybackState playbackState;
        private Thread playThread;
        private WaveFormat outputFormat;
        private bool dmoResamplerNeeded;
        private readonly SynchronizationContext syncContext;
        
        /// <summary>
        /// Playback Stopped
        /// </summary>
        public event EventHandler<StoppedEventArgs> PlaybackStopped;

        /// <summary>
        /// WASAPI Out using default audio endpoint
        /// </summary>
        /// <param name="shareMode">ShareMode - shared or exclusive</param>
        /// <param name="latency">Desired latency in milliseconds</param>
        public WasapiOut(AudioClientShareMode shareMode, int latency) :
            this(GetDefaultAudioEndpoint(), shareMode, true, latency)
        {

        }

        /// <summary>
        /// WASAPI Out using default audio endpoint
        /// </summary>
        /// <param name="shareMode">ShareMode - shared or exclusive</param>
        /// <param name="useEventSync">true if sync is done with event. false use sleep.</param>
        /// <param name="latency">Desired latency in milliseconds</param>
        public WasapiOut(AudioClientShareMode shareMode, bool useEventSync, int latency) :
            this(GetDefaultAudioEndpoint(), shareMode, useEventSync, latency)
        {

        }

        /// <summary>
        /// Creates a new WASAPI Output
        /// </summary>
        /// <param name="device">Device to use</param>
        /// <param name="shareMode"></param>
        /// <param name="useEventSync">true if sync is done with event. false use sleep.</param>
        /// <param name="latency"></param>
        public WasapiOut(MMDevice device, AudioClientShareMode shareMode, bool useEventSync, int latency)
        {
            this.audioClient = device.AudioClient;
            this.mmDevice = device;
            this.shareMode = shareMode;
            this.isUsingEventSync = useEventSync;
            this.latencyMilliseconds = latency;
            this.syncContext = SynchronizationContext.Current;
        }

        static MMDevice GetDefaultAudioEndpoint()
        {
            if (Environment.OSVersion.Version.Major < 6)
            {
                throw new NotSupportedException("WASAPI supported only on Windows Vista and above");
            }
            var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }

        private void PlayThread()
        {
            ResamplerDmoStream resamplerDmoStream = null;
            IWaveProvider playbackProvider = this.sourceProvider;
            Exception exception = null;
            try
            {
                if (this.dmoResamplerNeeded)
                {
                    resamplerDmoStream = new ResamplerDmoStream(sourceProvider, outputFormat);
                    playbackProvider = resamplerDmoStream;
                }

                // fill a whole buffer
                bufferFrameCount = audioClient.BufferSize;
                bytesPerFrame = outputFormat.Channels * outputFormat.BitsPerSample / 8;
                readBuffer = new byte[bufferFrameCount * bytesPerFrame];
                FillBuffer(playbackProvider, bufferFrameCount);

                // Create WaitHandle for sync
                var waitHandles = new WaitHandle[] { frameEventWaitHandle };

                audioClient.Start();

                while (playbackState != PlaybackState.Stopped)
                {
                    // If using Event Sync, Wait for notification from AudioClient or Sleep half latency
                    int indexHandle = 0;
                    if (isUsingEventSync)
                    {
                        indexHandle = WaitHandle.WaitAny(waitHandles, 3 * latencyMilliseconds, false);
                    }
                    else
                    {
                        Thread.Sleep(latencyMilliseconds / 2);
                    }

                    // If still playing and notification is ok
                    if (playbackState == PlaybackState.Playing && indexHandle != WaitHandle.WaitTimeout)
                    {
                        // See how much buffer space is available.
                        int numFramesPadding = 0;
                        if (isUsingEventSync)
                        {
                            // In exclusive mode, always ask the max = bufferFrameCount = audioClient.BufferSize
                            numFramesPadding = (shareMode == AudioClientShareMode.Shared) ? audioClient.CurrentPadding : 0;
                        }
                        else
                        {
                            numFramesPadding = audioClient.CurrentPadding;
                        }
                        int numFramesAvailable = bufferFrameCount - numFramesPadding;
                        if (numFramesAvailable > 10) // see https://naudio.codeplex.com/workitem/16363
                        {
                            FillBuffer(playbackProvider, numFramesAvailable);
                        }
                    }
                }
                Thread.Sleep(latencyMilliseconds / 2);
                audioClient.Stop();
                if (playbackState == PlaybackState.Stopped)
                {
                    audioClient.Reset();
                }
            }
            catch (Exception e)
            {
                exception = e;
            }
            finally
            {
                if (resamplerDmoStream != null)
                {
                    resamplerDmoStream.Dispose();
                }
                RaisePlaybackStopped(exception);
            }
        }

        private void RaisePlaybackStopped(Exception e)
        {
            var handler = PlaybackStopped;
            if (handler != null)
            {
                if (this.syncContext == null)
                {
                    handler(this, new StoppedEventArgs(e));
                }
                else
                {
                    syncContext.Post(state => handler(this, new StoppedEventArgs(e)), null);
                }
            }
        }

        private void FillBuffer(IWaveProvider playbackProvider, int frameCount)
        {
            IntPtr buffer = renderClient.GetBuffer(frameCount);
            int readLength = frameCount * bytesPerFrame;
            int read = playbackProvider.Read(readBuffer, 0, readLength);
            if (read == 0)
            {
                playbackState = PlaybackState.Stopped;
            }
            Marshal.Copy(readBuffer,0,buffer,read);
            int actualFrameCount = read / bytesPerFrame;
            /*if (actualFrameCount != frameCount)
            {
                Debug.WriteLine(String.Format("WASAPI wanted {0} frames, supplied {1}", frameCount, actualFrameCount ));
            }*/
            renderClient.ReleaseBuffer(actualFrameCount, AudioClientBufferFlags.None);
        }

        /// <summary>
        /// Gets the current position in bytes from the wave output device.
        /// (n.b. this is not the same thing as the position within your reader
        /// stream)
        /// </summary>
        /// <returns>Position in bytes</returns>
        public long GetPosition()
        {
            if (playbackState == Wave.PlaybackState.Stopped)
            {
                return 0;
            }
            return (long)audioClient.AudioClockClient.AdjustedPosition;
        }

        /// <summary>
        /// Gets a <see cref="Wave.WaveFormat"/> instance indicating the format the hardware is using.
        /// </summary>
        public WaveFormat OutputWaveFormat
        {
            get { return outputFormat; }
        }

        #region IWavePlayer Members

        /// <summary>
        /// Begin Playback
        /// </summary>
        public void Play()
        {
            if (playbackState != PlaybackState.Playing)
            {
                if (playbackState == PlaybackState.Stopped)
                {
                    playThread = new Thread(PlayThread);
                    playbackState = PlaybackState.Playing;
                    playThread.Start();                    
                }
                else
                {
                    playbackState = PlaybackState.Playing;
                }

                
            }
        }

        /// <summary>
        /// Stop playback and flush buffers
        /// </summary>
        public void Stop()
        {
            if (playbackState != PlaybackState.Stopped)
            {
                playbackState = PlaybackState.Stopped;
                playThread.Join();
                playThread = null;
            }
        }

        /// <summary>
        /// Stop playback without flushing buffers
        /// </summary>
        public void Pause()
        {
            if (playbackState == PlaybackState.Playing)
            {
                playbackState = PlaybackState.Paused;
            }
            
        }

        /// <summary>
        /// Initialize for playing the specified wave stream
        /// </summary>
        /// <param name="waveProvider">IWaveProvider to play</param>
        public void Init(IWaveProvider waveProvider)
        {
            long latencyRefTimes = latencyMilliseconds * 10000;
            outputFormat = waveProvider.WaveFormat;
            // first attempt uses the WaveFormat from the WaveStream
            WaveFormatExtensible closestSampleRateFormat;
            if (!audioClient.IsFormatSupported(shareMode, outputFormat, out closestSampleRateFormat))
            {
                // Use closesSampleRateFormat (in sharedMode, it equals usualy to the audioClient.MixFormat)
                // See documentation : http://msdn.microsoft.com/en-us/library/ms678737(VS.85).aspx 
                // They say : "In shared mode, the audio engine always supports the mix format"
                // The MixFormat is more likely to be a WaveFormatExtensible.
                if (closestSampleRateFormat == null)
                {
                    WaveFormat correctSampleRateFormat = audioClient.MixFormat;
                        /*WaveFormat.CreateIeeeFloatWaveFormat(
                        audioClient.MixFormat.SampleRate,
                        audioClient.MixFormat.Channels);*/

                    if (!audioClient.IsFormatSupported(shareMode, correctSampleRateFormat))
                    {
                        // Iterate from Worst to Best Format
                        WaveFormatExtensible[] bestToWorstFormats = {
                                  new WaveFormatExtensible(
                                      outputFormat.SampleRate, 32,
                                      outputFormat.Channels),
                                  new WaveFormatExtensible(
                                      outputFormat.SampleRate, 24,
                                      outputFormat.Channels),
                                  new WaveFormatExtensible(
                                      outputFormat.SampleRate, 16,
                                      outputFormat.Channels),
                              };

                        // Check from best Format to worst format ( Float32, Int24, Int16 )
                        for (int i = 0; i < bestToWorstFormats.Length; i++ )
                        {
                            correctSampleRateFormat = bestToWorstFormats[i];
                            if ( audioClient.IsFormatSupported(shareMode, correctSampleRateFormat) )
                            {
                                break;
                            }
                            correctSampleRateFormat = null;
                        }

                        // If still null, then test on the PCM16, 2 channels
                        if (correctSampleRateFormat == null)
                        {
                            // Last Last Last Chance (Thanks WASAPI)
                            correctSampleRateFormat = new WaveFormatExtensible(outputFormat.SampleRate, 16, 2);
                            if (!audioClient.IsFormatSupported(shareMode, correctSampleRateFormat))
                            {
                                throw new NotSupportedException("Can't find a supported format to use");
                            }
                        }
                    }
                    outputFormat = correctSampleRateFormat;
                }
                else
                {
                    outputFormat = closestSampleRateFormat;
                }

                // just check that we can make it.
                using (new ResamplerDmoStream(waveProvider, outputFormat))
                {
                }
                this.dmoResamplerNeeded = true;
            }
            else
            {
                dmoResamplerNeeded = false;
            }
            this.sourceProvider = waveProvider;

            // If using EventSync, setup is specific with shareMode
            if (isUsingEventSync)
            {
                // Init Shared or Exclusive
                if (shareMode == AudioClientShareMode.Shared)
                {
                    // With EventCallBack and Shared, both latencies must be set to 0
                    audioClient.Initialize(shareMode, AudioClientStreamFlags.EventCallback, 0, 0,
                        outputFormat, Guid.Empty);

                    // Get back the effective latency from AudioClient
                    latencyMilliseconds = (int)(audioClient.StreamLatency / 10000);
                }
                else
                {
                    // With EventCallBack and Exclusive, both latencies must equals
                    audioClient.Initialize(shareMode, AudioClientStreamFlags.EventCallback, latencyRefTimes, latencyRefTimes,
                                        outputFormat, Guid.Empty);
                }

                // Create the Wait Event Handle
                frameEventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
                audioClient.SetEventHandle(frameEventWaitHandle.SafeWaitHandle.DangerousGetHandle());
            }
            else
            {
                // Normal setup for both sharedMode
                audioClient.Initialize(shareMode, AudioClientStreamFlags.None, latencyRefTimes, 0,
                                    outputFormat, Guid.Empty);
            }

            // Get the RenderClient
            renderClient = audioClient.AudioRenderClient;
        }

        /// <summary>
        /// Playback State
        /// </summary>
        public PlaybackState PlaybackState
        {
            get { return playbackState; }
        }

        /// <summary>
        /// Volume
        /// </summary>
        public float Volume
        {
            get
            {
                return mmDevice.AudioEndpointVolume.MasterVolumeLevelScalar;                                
            }
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException("value", "Volume must be between 0.0 and 1.0");
                if (value > 1) throw new ArgumentOutOfRangeException("value", "Volume must be between 0.0 and 1.0");
                mmDevice.AudioEndpointVolume.MasterVolumeLevelScalar = value;
            }
        }

        /// <summary>
        /// Retrieve the AudioStreamVolume object for this audio stream
        /// </summary>
        /// <remarks>
        /// This returns the AudioStreamVolume object ONLY for shared audio streams.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// This is thrown when an exclusive audio stream is being used.
        /// </exception>
        public AudioStreamVolume AudioStreamVolume
        {
            get 
            {
                if (shareMode == AudioClientShareMode.Exclusive)
                {
                    throw new InvalidOperationException("AudioStreamVolume is ONLY supported for shared audio streams.");
                }
                return audioClient.AudioStreamVolume;  
            }
        }

        #endregion

        #region IDisposable Members

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
            if (audioClient != null)
            {
                Stop();

                audioClient.Dispose();
                audioClient = null;
                renderClient = null;
            }

        }

        #endregion
    }
}
