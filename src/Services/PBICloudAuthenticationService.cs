﻿using Microsoft.AspNetCore.Hosting;
using Microsoft.Identity.Client;
using Sqlbi.Bravo.Infrastructure.Authentication;
using Sqlbi.Bravo.Infrastructure.Helpers;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sqlbi.Bravo.Services
{
    internal class PBICloudAuthenticationService : IPBICloudAuthenticationService
    {
        private const string MicrosoftAccountOnlyQueryParameter = "msafed=0";

        // AuthorityUri     "https://login.microsoftonline.com/common"
        // ClientId         "ea0616ba-638b-4df5-95b9-636659ae5121"
        // EndpointUri  "https://api.powerbi.com"
        // Name         "Public"
        // RedirectUri      "https://login.microsoftonline.com/common/oauth2/nativeclient"
        // ResourceUri  "https://analysis.windows.net/powerbi/api"
        // Scopes       { "https://analysis.windows.net/powerbi/api/.default" }

        private static readonly PublicClientApplicationOptions _options = new()
        {
            Instance = "https://login.microsoftonline.com/",
            TenantId = "common",
            ClientId = "ea0616ba-638b-4df5-95b9-636659ae5121",
            RedirectUri = "https://login.microsoftonline.com/common/oauth2/nativeclient"
        };

        private static readonly string[] _scopes =
        {
            "https://analysis.windows.net/powerbi/api/.default"
        };

        private static readonly IPublicClientApplication _application;
        private static readonly SemaphoreSlim _tokenSemaphore = new(1);

        static PBICloudAuthenticationService()
        {
            _application = PublicClientApplicationBuilder.Create(_options.ClientId)
                .WithAuthority($"{ _options.Instance }{ _options.TenantId }")
                .WithDefaultRedirectUri()
                .Build();

            TokenCacheHelper.EnableSerialization(_application.UserTokenCache);
        }

        private readonly IWebHostEnvironment _environment;
        private readonly CustomWebViewOptions _customWebViewOptions;
        private AuthenticationResult? _authenticationResult;

        public PBICloudAuthenticationService(IWebHostEnvironment environment)
        {
            _environment = environment;
            _customWebViewOptions = new CustomWebViewOptions(_environment.WebRootPath);
        }

        public AuthenticationResult? CurrentAuthentication => _authenticationResult;

        /// <summary>
        /// Removes all account information from MSAL's token cache, removes app-only (not OS-wide) and does not affect the browser cookies
        /// </summary>
        public async Task ClearTokenCacheAsync()
        {
            await _tokenSemaphore.WaitAsync();
            try
            {
                var accounts = (await _application.GetAccountsAsync().ConfigureAwait(false)).ToArray();

                foreach (var account in accounts)
                    await _application.RemoveAsync(account).ConfigureAwait(false);

                _authenticationResult = null;
            }
            finally
            {
                _tokenSemaphore.Release();
            }
        }

        public async Task AcquireTokenAsync(TimeSpan cancelAfter, string? identifier = null)
        {
            await _tokenSemaphore.WaitAsync();
            try
            {
                using var cancellationTokenSource = new CancellationTokenSource(cancelAfter);

                _authenticationResult = await InternalAcquireTokenAsync(cancellationTokenSource.Token, identifier).ConfigureAwait(false);
            }
            finally
            {
                _tokenSemaphore.Release();
            }
        }

        /// <summary>
        /// https://docs.microsoft.com/en-us/azure/active-directory/develop/scenario-desktop-acquire-token?tabs=dotnet
        /// </summary>
        private async Task<AuthenticationResult> InternalAcquireTokenAsync(CancellationToken cancellationToken, string? identifier = null)
        {
            // Use account used to signed-in in Windows (WAM). WAM will always get an account in the cache.
            // So if we want to have a chance to select the accounts interactively, we need to force the non-account
            //account = PublicClientApplication.OperatingSystemAccount;

            // Use one of the Accounts known by Windows (WAM). We force WAM to display the dialog with the accounts
            //account = null;

            var account = await _application.GetAccountAsync(identifier).ConfigureAwait(false);
            
            try
            {
                // Try to acquire an access token from the cache, if UI interaction is required, MsalUiRequiredException will be thrown.
                var authenticationResult = await _application.AcquireTokenSilent(_scopes, account).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                return authenticationResult;
            }
            catch (MsalUiRequiredException /* murex */)
            {
                try
                {
                    var builder = _application.AcquireTokenInteractive(_scopes)
                        //.WithAccount(account)
                        //.WithClaims(murex.Claims)
                        //.WithParentActivityOrWindow( /* new WindowInteropHelper(Program.HostWindow).Handle */ Program.HostWindow!.WindowHandle) // optional, used to center the browser on the window
                        //.WithPrompt(Prompt.SelectAccount) // Force a sign-in (Prompt.SelectAccount), as the MSAL web browser might contain cookies for the current user and we don't necessarily want to re-sign-in the same user 
                        .WithExtraQueryParameters(MicrosoftAccountOnlyQueryParameter)
                        .WithSystemWebViewOptions(_customWebViewOptions);

                    //if (!_application.IsEmbeddedWebViewAvailable())
                    //{
                    // You app should install the embedded browser WebView2 https://aka.ms/msal-net-webview2
                    // but if for some reason this is not possible, you can fall back to the system browser 
                    // in this case, the redirect uri needs to be set to "http://localhost"
                    //builder = builder.WithUseEmbeddedWebView(useEmbeddedWebView: false);
                    //}

                    //builder = builder.WithSystemWebViewOptions(GetCustomWebViewOptions()); // Using the custom html
                    //builder = builder.WithCustomWebUi(customWebUi: new LoginWebUI(Program.HostWindow.WindowHandle)); //Using our custom web ui

                    var authenticationResult = await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    return authenticationResult;
                }
                catch (MsalException) // ex.ErrorCode => Microsoft.Identity.Client.MsalError
                {
                    throw;
                }
            }
        }
    }
}
