namespace Jellyfin.Plugin.HAnimeTV.Hanime
{
    /// <summary>
    /// hanime.tv could not be reached or returned something unusable.
    /// </summary>
    public class HanimeException : Exception
    {
        public HanimeException(string message)
            : base(message)
        {
        }

        public HanimeException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
