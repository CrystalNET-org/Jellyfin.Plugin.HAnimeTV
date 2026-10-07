using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Channels
{
    /// <summary>
    /// A channel video's playable source: the plugin's stream link, which serves the
    /// provider's stream (HLS, or an MP4 file) through Jellyfin, probed for its codecs and duration.
    /// </summary>
    public static class StreamSource
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Creates the source and probes it; if the probe fails, H.264 and AAC are assumed.
        /// </summary>
        public static async Task<MediaSourceInfo> CreateAsync(string id, string streamLink, long? runTimeTicks, IMediaEncoder mediaEncoder, ILogger logger, CancellationToken cancellationToken, string container = "hls")
        {
            var source = Create(id, streamLink, runTimeTicks, container);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ProbeTimeout);
                var info = await mediaEncoder.GetMediaInfo(
                    new MediaInfoRequest { MediaSource = Create(id, streamLink, runTimeTicks, container), MediaType = DlnaProfileType.Video, ExtractChapters = false },
                    timeout.Token).ConfigureAwait(false);
                ApplyProbe(source, info);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Adult Media: could not probe the stream of {Id}, assuming H.264 and AAC: {Error}", id, ex.Message);
            }

            return source;
        }

        internal static MediaSourceInfo Create(string id, string streamLink, long? runTimeTicks, string container = "hls") => new()
        {
            Id = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(id))),
            Path = streamLink,
            Protocol = MediaProtocol.Http,
            Container = container,
            IsRemote = true,
            Type = MediaSourceType.Default,
            // The link needs no headers: browsers play it themselves, Jellyfin's ffmpeg remuxes it
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = false,
            RunTimeTicks = runTimeTicks,
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", IsDefault = true },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Channels = 2, ChannelLayout = "stereo", IsDefault = true },
            ],
            DefaultAudioStreamIndex = 1,
        };

        internal static void ApplyProbe(MediaSourceInfo source, MediaSourceInfo probed)
        {
            if (probed.MediaStreams?.Any(s => s.Type == MediaStreamType.Video) == true)
            {
                source.MediaStreams = probed.MediaStreams;
                source.DefaultAudioStreamIndex = probed.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio)?.Index;
            }

            if (probed.RunTimeTicks is > 0)
            {
                source.RunTimeTicks = probed.RunTimeTicks;
            }

            if (probed.Bitrate is > 0)
            {
                source.Bitrate = probed.Bitrate;
            }
        }
    }
}
