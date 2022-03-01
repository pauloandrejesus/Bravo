namespace Sqlbi.Bravo.Infrastructure.Configuration.Settings
{
    using Sqlbi.Bravo.Infrastructure.Extensions;
    using Sqlbi.Bravo.Infrastructure.Security;
    using System;
    using System.Linq;
    using System.Net;
    using System.Text.Json.Serialization;

    public class ProxySettings
    {
        [JsonPropertyName("proxyType")]
        public ProxyType ProxyType { get; set; } = ProxyType.System;

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

        /// <summary>
        /// Indicates whether to bypass the proxy server for local addresses. The default value is true.
        /// </summary>
        [JsonPropertyName("bypassOnLocal")]
        public bool BypassOnLocal { get; set; } = true;

        /// <summary>
        /// An array of addresses that do not use the proxy server
        /// </summary>
        [JsonPropertyName("bypassList")]
        public string[]? BypassList { get; set; }

        internal ICredentials? GetCredentials()
        {
            if (!UseDefaultCredentials)
            {
                if (CredentialManager.TryGetCredential(targetName: AppEnvironment.CredentialManagerProxyCredentialName, out var genericCredential))
                {
                    var credentials = genericCredential.ToNetworkCredential();
                    return credentials;
                }
                
            }

            return CredentialCache.DefaultCredentials;
        }

        internal string[]? GetSafeBypassList()
        {
            var bypassList = BypassList?.ToList();
            _ = bypassList?.RemoveAll("<-loopback>".EqualsI); // Remove this special proxy bypass rule which has the effect of subtracting the implicit loopback rules
            
            return bypassList?.ToArray();
        }
    }

    public enum ProxyType
    {
        /// <summary>
        /// Specifies not to use a Proxy, even if the system is otherwise configured to use one. It overrides and ignore any other proxy settings that are provided
        /// </summary>
        None = 0,

        /// <summary>
        /// Specifies to try and automatically detect the system proxy configuration. This is the default value
        /// </summary>
        System = 1,

        /// <summary>
        /// Specifies to use a custom proxy configuration and applies all other proxy settings that are provided
        /// </summary>
        Custom = 2,
    }
}
