using System.Text.Json.Serialization;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.HAnimeTV.Configuration
{
    /// <summary>
    /// Plugin settings: shared ones, and a section per provider.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets a value indicating whether the channel and library access in the
        /// users' policies (Dashboard → Users → Access) follows the providers' user selections.
        /// </summary>
        public bool EnforceAccess { get; set; } = true;

        /// <summary>
        /// Gets or sets the address of this Jellyfin server in the stream links, e.g.
        /// https://jellyfin.example.com. Browsers play the links directly and Jellyfin's ffmpeg
        /// (and its ffmpeg workers) start streams from them, so all of them must reach it.
        /// Empty means Jellyfin's guess of its own address.
        /// </summary>
        public string StreamBaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the secret in the stream links. It only allows streaming the providers'
        /// videos through this server; generated on first use.
        /// </summary>
        public string StreamToken { get; set; } = string.Empty;

        public HentaiSettings Hentai { get; set; } = new();

        public PornhubSettings Pornhub { get; set; } = new();

        /// <summary>
        /// Moves the settings of versions before 0.2, which only knew hanime.tv and kept its
        /// settings at the top level, into <see cref="Hentai"/>.
        /// </summary>
        /// <returns>Whether anything was moved.</returns>
        public bool MigrateLegacySettings()
        {
            if (LegacyAllowedUsers is null && LegacyHiddenTags is null && LegacyHideCensored is null && LegacyEmail is null
                && LegacyPassword is null && LegacySearchUrl is null && LegacyHandshakeUrl is null && LegacyLoginUrl is null
                && LegacyStreamHost is null && LegacyCatalogCacheHours is null && LegacyLibraryPath is null
                && LegacyCreateLibrary is null && LegacyLibraryName is null)
            {
                return false;
            }

            Hentai ??= new HentaiSettings();
            Hentai.AllowedUsers = LegacyAllowedUsers ?? Hentai.AllowedUsers;
            Hentai.HiddenTags = LegacyHiddenTags ?? Hentai.HiddenTags;
            Hentai.HideCensored = LegacyHideCensored ?? Hentai.HideCensored;
            Hentai.Email = LegacyEmail ?? Hentai.Email;
            Hentai.Password = LegacyPassword ?? Hentai.Password;
            Hentai.SearchUrl = LegacySearchUrl ?? Hentai.SearchUrl;
            Hentai.HandshakeUrl = LegacyHandshakeUrl ?? Hentai.HandshakeUrl;
            Hentai.LoginUrl = LegacyLoginUrl ?? Hentai.LoginUrl;
            Hentai.StreamHost = LegacyStreamHost ?? Hentai.StreamHost;
            Hentai.CatalogCacheHours = LegacyCatalogCacheHours ?? Hentai.CatalogCacheHours;
            Hentai.LibraryPath = LegacyLibraryPath ?? Hentai.LibraryPath;
            Hentai.CreateLibrary = LegacyCreateLibrary ?? Hentai.CreateLibrary;
            Hentai.LibraryName = LegacyLibraryName ?? Hentai.LibraryName;
            // The library is what versions before 0.2 had last
            Hentai.Mode = ProviderMode.Library;

            LegacyAllowedUsers = null;
            LegacyHiddenTags = null;
            LegacyHideCensored = null;
            LegacyEmail = null;
            LegacyPassword = null;
            LegacySearchUrl = null;
            LegacyHandshakeUrl = null;
            LegacyLoginUrl = null;
            LegacyStreamHost = null;
            LegacyCatalogCacheHours = null;
            LegacyLibraryPath = null;
            LegacyCreateLibrary = null;
            LegacyLibraryName = null;
            return true;
        }

        // Settings of versions before 0.2: read from their files, never written

        // Arrays as Jellyfin writes them: <AllowedUsers><guid>…</guid></AllowedUsers>
        [XmlArray("AllowedUsers")]
        [JsonIgnore]
        public Guid[]? LegacyAllowedUsers { get; set; }

        [XmlArray("HiddenTags")]
        [JsonIgnore]
        public string[]? LegacyHiddenTags { get; set; }

        [XmlElement("HideCensored")]
        [JsonIgnore]
        public bool? LegacyHideCensored { get; set; }

        [XmlElement("Email")]
        [JsonIgnore]
        public string? LegacyEmail { get; set; }

        [XmlElement("Password")]
        [JsonIgnore]
        public string? LegacyPassword { get; set; }

        [XmlElement("SearchUrl")]
        [JsonIgnore]
        public string? LegacySearchUrl { get; set; }

        [XmlElement("HandshakeUrl")]
        [JsonIgnore]
        public string? LegacyHandshakeUrl { get; set; }

        [XmlElement("LoginUrl")]
        [JsonIgnore]
        public string? LegacyLoginUrl { get; set; }

        [XmlElement("StreamHost")]
        [JsonIgnore]
        public string? LegacyStreamHost { get; set; }

        [XmlElement("CatalogCacheHours")]
        [JsonIgnore]
        public int? LegacyCatalogCacheHours { get; set; }

        [XmlElement("LibraryPath")]
        [JsonIgnore]
        public string? LegacyLibraryPath { get; set; }

        [XmlElement("CreateLibrary")]
        [JsonIgnore]
        public bool? LegacyCreateLibrary { get; set; }

        [XmlElement("LibraryName")]
        [JsonIgnore]
        public string? LegacyLibraryName { get; set; }

        public bool ShouldSerializeLegacyAllowedUsers() => false;

        public bool ShouldSerializeLegacyHiddenTags() => false;

        public bool ShouldSerializeLegacyHideCensored() => false;

        public bool ShouldSerializeLegacyEmail() => false;

        public bool ShouldSerializeLegacyPassword() => false;

        public bool ShouldSerializeLegacySearchUrl() => false;

        public bool ShouldSerializeLegacyHandshakeUrl() => false;

        public bool ShouldSerializeLegacyLoginUrl() => false;

        public bool ShouldSerializeLegacyStreamHost() => false;

        public bool ShouldSerializeLegacyCatalogCacheHours() => false;

        public bool ShouldSerializeLegacyLibraryPath() => false;

        public bool ShouldSerializeLegacyCreateLibrary() => false;

        public bool ShouldSerializeLegacyLibraryName() => false;
    }
}
