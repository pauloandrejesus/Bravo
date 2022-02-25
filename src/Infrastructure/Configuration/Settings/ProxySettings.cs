namespace Sqlbi.Bravo.Infrastructure.Configuration.Settings
{
    using System;
    using System.Net;
    using System.Text.Json.Serialization;

    public class ProxySettings
    {
        /// <summary>
        /// Indicates whether to automatically detect and use the system proxy configuration instead of using a provided one
        /// </summary>
        [JsonPropertyName("useSystemProxy")]
        public bool UseSystemProxy { get; set; } = true;

        /// <summary>
        /// Indicate whether the <see cref="CredentialCache.DefaultCredentials"/> system credentials of the application are sent with requests.
        /// </summary>
        [JsonPropertyName("useDefaultCredentials")]
        public bool UseDefaultCredentials { get; set; } = true;

        /// <summary>
        /// The address of the proxy server.
        /// </summary>
        [JsonPropertyName("address")]
        public Uri? Address { get; set; }

        [JsonPropertyName("domain")]
        public string? Domain { get; set; }

        [JsonPropertyName("userName")]
        public string? UserName { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        /// <summary>
        /// Indicates whether to bypass the proxy server for local addresses. The default value is true.
        /// </summary>
        [JsonPropertyName("bypassOnLocal")]
        public bool BypassOnLocal { get; set; } = false;

        /// <summary>
        /// An array of addresses that do not use the proxy server
        /// </summary>
        [JsonPropertyName("bypassList")]
        public string[]? BypassList { get; set; }

        //[JsonIgnore]
        //internal string? WebView2CommandlineOptions
        //{
        //    get 
        //    {
        //        // TODO: https://docs.microsoft.com/en-us/deployedge/edge-learnmore-cmdline-options-proxy-settings?WT.mc_id=DT-MVP-5003235#command-line-options-for-proxy-settings
        //        throw new NotImplementedException();
        //    }
        //}

        //public bool HasUsernameAndPassword => !Username.IsNullOrEmpty() && !Password.IsNullOrEmpty();
    }
}
