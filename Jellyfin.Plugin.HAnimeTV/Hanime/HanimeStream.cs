namespace Jellyfin.Plugin.HAnimeTV.Hanime
{
    /// <summary>
    /// A playable HLS stream of a video.
    /// </summary>
    /// <param name="Url">Absolute URL of the HLS playlist.</param>
    /// <param name="Height">Vertical resolution, 0 if unknown.</param>
    /// <param name="Premium">Whether the stream needs a premium account.</param>
    public sealed record HanimeStream(string Url, int Height, bool Premium)
    {
        public string Label => Height > 0 ? Height + "p" : "Auto";
    }
}
