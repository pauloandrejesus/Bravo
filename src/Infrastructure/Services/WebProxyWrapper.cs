namespace Sqlbi.Bravo.Infrastructure.Configuration.Settings
{
    using System;
    using System.Net;

    internal class WebProxyWrapper : IWebProxy
    {
        public static readonly WebProxyWrapper Current = new();

        private static readonly object _customProxyLock = new();
        private readonly IWebProxy _systemProxy;
        private IWebProxy? _customProxy;

        private WebProxyWrapper()
        {
            // For .NET Core The initial value of the static property HttpClient.DefaultProxy represents the system proxy on the machine.
            // Do not use legacy WebRequest.GetSystemWebProxy() or WebProxy.GetDefaultProxy(), as these return a proxy configured with the Internet Explorer settings
            _systemProxy = System.Net.Http.HttpClient.DefaultProxy;
        }

        #region IWebProxy

        public ICredentials? Credentials
        { 
            get
            {
                var webProxy = GetWebProxy();
                return webProxy.Credentials;
            }
            set
            {
                var webProxy = GetWebProxy();
                if (webProxy.Credentials != value)
                    webProxy.Credentials = value;
            }
        }

        public Uri? GetProxy(Uri destination)
        {
            var webProxy = GetWebProxy();
            var proxyUri = webProxy.GetProxy(destination);

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
            if (UserPreferences.Current.Proxy is null || UserPreferences.Current.Proxy.UseSystemProxy)
            {
                return _systemProxy;
            }
            else
            {
                if (_customProxy is null)
                {
                    lock (_customProxyLock)
                    {
                        if (_customProxy is null)
                        {
                            var settings = UserPreferences.Current.Proxy;

                            var credentials = settings.UseDefaultCredentials 
                                ? new NetworkCredential(settings.UserName, settings.Password, settings.Domain) 
                                : CredentialCache.DefaultCredentials;

                            _customProxy = new WebProxy(settings.Address, settings.BypassOnLocal, settings.BypassList, credentials);
                        }
                    }
                }

                return _customProxy;
            } 
        }
    }
}
