namespace Sqlbi.Bravo.Infrastructure.Services
{
    using Sqlbi.Bravo.Infrastructure.Configuration;
    using Sqlbi.Bravo.Infrastructure.Configuration.Settings;
    using Sqlbi.Bravo.Infrastructure.Extensions;
    using Sqlbi.Bravo.Infrastructure.Helpers;
    using Sqlbi.Bravo.Infrastructure.Security;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Reflection;

    /// <summary>
    /// Implements a <see cref="WebProxy"/> wrapper that supports the system proxy on the machine or a single manual configured proxy server
    /// </summary>
    /// <remarks>
    /// Proxy resolution through the WPAD auto-detect protocol and the Proxy Automatic Configuration (PAC) files is not currently supported
    /// </remarks>
    internal class WebProxyWrapper : IWebProxy
    {
        public static readonly WebProxyWrapper Current = new();

        private static readonly object _proxyLock = new();
        private readonly IWebProxy _systemProxy;
        private IWebProxy? _customProxy;
        private IWebProxy? _noProxy;

        private WebProxyWrapper()
        {
            _systemProxy = new HttpSystemProxy();

            WebView2Helper.SetWebView2CmdlineProxyArguments(UserPreferences.Current.Proxy, (HttpSystemProxy)_systemProxy);
        }

        #region IWebProxy

        public ICredentials? Credentials
        { 
            get
            {
                var webProxy = GetWebProxy();
                return webProxy?.Credentials;
            }
            set
            {
                var webProxy = GetWebProxy();
                if (webProxy is not null && webProxy.Credentials != value)
                    webProxy.Credentials = value;
            }
        }

        public Uri? GetProxy(Uri destination)
        {
            var webProxy = GetWebProxy();
            var proxyUri = webProxy?.GetProxy(destination);

            return proxyUri;
        }

        public bool IsBypassed(Uri host)
        {
            var webProxy = GetWebProxy();
            var isBypassed = webProxy.IsBypassed(host);

            return isBypassed;
        }

        #endregion

        private IWebProxy GetWebProxy()
        {
            if (UserPreferences.Current.Proxy is null || UserPreferences.Current.Proxy.ProxyType == ProxyType.System)
            {
                return _systemProxy;
            }
            else if (UserPreferences.Current.Proxy.ProxyType == ProxyType.None)
            {
                if (_noProxy is null)
                {
                    lock (_proxyLock)
                    {
                        if (_noProxy is null)
                        {
                            _noProxy = new HttpNoProxy();
                        }
                    }
                }

                return _noProxy;
            }
            else
            {
                if (_customProxy is null)
                {
                    lock (_proxyLock)
                    {
                        if (_customProxy is null)
                        {
                            var settings = UserPreferences.Current.Proxy;
                            var credentials = CredentialCache.DefaultCredentials;

                            if (!settings.UseDefaultCredentials && CredentialManager.TryGetCredential(targetName: AppEnvironment.CredentialManagerProxyCredentialName, out var genericCredential))
                            {
                                credentials = genericCredential.ToNetworkCredential();
                            }

                            var bypassList = settings.BypassList?.ToList();
                            _ = bypassList?.RemoveAll("<-loopback>".EqualsI); // Remove this special proxy bypass rule which has the effect of subtracting the implicit loopback rules
                            var bypassListSecured = bypassList?.ToArray();

                            _customProxy = new WebProxy(settings.Address, settings.BypassOnLocal, bypassListSecured, credentials);
                        }
                    }
                }

                return _customProxy;
            }
        }
    }

    internal class HttpSystemProxy : IWebProxy
    {
        private readonly IWebProxy _systemProxy;

        public HttpSystemProxy()
        {
            // For .NET Core The initial value of the static property HttpClient.DefaultProxy represents the system proxy on the machine.
            // Do not use legacy WebRequest.GetSystemWebProxy() or WebProxy.GetDefaultProxy(), as these return a proxy configured with the Internet Explorer settings
            _systemProxy = System.Net.Http.HttpClient.DefaultProxy;


            var systemProxyType = _systemProxy.GetType();
            if (systemProxyType.FullName == "System.Net.Http.HttpEnvironmentProxy")
            {
                var bypassObject = systemProxyType.GetField("_bypass", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_systemProxy);
                if (bypassObject is IEnumerable<string> bypass)
                {
                    BypassList = bypass.ToArray();
                }

                var httpProxyUriObject = systemProxyType.GetField("_httpProxyUri", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_systemProxy);
                if (httpProxyUriObject is Uri httpProxyUri)
                {
                    HttpProxyUris = new[] { httpProxyUri };
                }

                var httpsProxyUriObject = systemProxyType.GetField("_httpsProxyUri", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_systemProxy);
                if (httpsProxyUriObject is Uri httpsProxyUri)
                {
                    HttpsProxyUris = new[] { httpsProxyUri };
                }
            }
            else if (systemProxyType.FullName == "System.Net.Http.HttpWindowsProxy")
            {
                var bypassObject = systemProxyType.GetField("_bypass", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_systemProxy);
                if (bypassObject is IEnumerable<string> bypass)
                {
                    BypassList = bypass.ToArray();
                }

                var insecureProxyObject = systemProxyType.GetField("_insecureProxy", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_systemProxy);
                var insecureProxyType = insecureProxyObject?.GetType();
                if (insecureProxyType?.FullName == "System.Net.Http.MultiProxy")
                {
                    var insecureProxyUrisObject = insecureProxyType.GetField("_uris", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(insecureProxyObject);
                    if (insecureProxyUrisObject is IEnumerable<Uri> insecureProxyUris)
                    {
                        HttpsProxyUris = insecureProxyUris.ToArray();
                    }
                }

                var secureProxyObject = systemProxyType.GetField("_secureProxy", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_systemProxy);
                var secureProxyType = secureProxyObject?.GetType();
                if (secureProxyType?.FullName == "System.Net.Http.MultiProxy")
                {
                    var secureProxyUrisObject = secureProxyType.GetField("_uris", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(secureProxyObject);
                    if (secureProxyUrisObject is IEnumerable<Uri> secureProxyUris)
                    {
                        HttpProxyUris = secureProxyUris.ToArray();
                    }
                }
            }
            else
            {
                // LOG not supported:
            }
        }

        public string[]? BypassList { get; private set; }

        public Uri[]? HttpProxyUris { get; private set; }

        public Uri[]? HttpsProxyUris { get; private set; }

        public ICredentials? Credentials
        { 
            get => _systemProxy.Credentials;
            set => _systemProxy.Credentials = value;
        }

        public Uri? GetProxy(Uri destination)
        {
            var proxyUri = _systemProxy.GetProxy(destination);
            return proxyUri;
        }

        public bool IsBypassed(Uri host)
        {
            var isBypassed = _systemProxy.IsBypassed(host);
            return isBypassed;
        }
    }

    internal class HttpNoProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => null;

        public bool IsBypassed(Uri host) => true; // No proxy, always bypassed
    }
}
