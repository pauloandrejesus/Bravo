namespace Sqlbi.Bravo.Infrastructure.Services.PowerBI
{
    using Microsoft.Identity.Client;
    using Sqlbi.Bravo.Infrastructure;
    using Sqlbi.Bravo.Infrastructure.Configuration;
    using Sqlbi.Bravo.Infrastructure.Helpers;
    using Sqlbi.Bravo.Infrastructure.Models;
    using Sqlbi.Bravo.Infrastructure.Models.PBICloud;
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IPBICloudAuthenticationService
    {
        IAuthenticationResult? AuthenticationResult { get; }

        Uri TenantCluster { get; }

        Task SignInAsync(string userPrincipalName, CancellationToken cancellationToken);

        Task SignOutAsync(CancellationToken cancellationToken);
    }

    internal class PBICloudAuthenticationService : IPBICloudAuthenticationService, IDisposable
    {
        private const string MicrosoftAccountOnlyQueryParameter = "msafed=0"; // Restrict logins to only AAD based organizational accounts

        private readonly IPBICloudSettingsService _pbicloudSettings;
        private readonly SemaphoreSlim _authenticationSemaphore = new(1, 1);

        private IPublicClientApplication? _publicClient;

        public PBICloudAuthenticationService(IPBICloudSettingsService pbicloudSetting)
        {
            _pbicloudSettings = pbicloudSetting;
        }

        private IPublicClientApplication PublicClient
        {
            get
            {
                BravoUnexpectedException.ThrowIfNull(_publicClient);
                return _publicClient;
            }
        }

        public IAuthenticationResult? AuthenticationResult { get; private set; }

        public Uri TenantCluster => new(_pbicloudSettings.TenantCluster.FixedClusterUri);

        public async Task SignInAsync(string userPrincipalName, CancellationToken cancellationToken)
        {
            await _authenticationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

                using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cancellationTokenSource.CancelAfter(AppEnvironment.MSALSignInTimeout);

                var previousAuthentication= AuthenticationResult;
                AuthenticationResult = await AcquireTokenAsync(userPrincipalName, cancellationTokenSource.Token).ConfigureAwait(false);

                var accountChanged = !AuthenticationResult.Equals(previousAuthentication);
                if (accountChanged)
                { 
                    await _pbicloudSettings.RefreshAsync(AuthenticationResult.AccessToken, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _authenticationSemaphore.Release();
            }
        }

        public async Task SignOutAsync(CancellationToken cancellationToken)
        {
            await _authenticationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AuthenticationResult = null;

                await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

                var accounts = (await PublicClient.GetAccountsAsync().ConfigureAwait(false)).ToArray();

                foreach (var account in accounts)
                {
                    await PublicClient.RemoveAsync(account).ConfigureAwait(false);
                }
            }
            finally
            {
                _authenticationSemaphore.Release();
            }
        }

        private async Task<IAuthenticationResult> AcquireTokenAsync(string? loginHint, CancellationToken cancellationToken)
        {
            // TODO: Acquire a token using WAM https://docs.microsoft.com/en-us/azure/active-directory/develop/scenario-desktop-acquire-token-wam
            // TODO: Acquire a token using integrated Windows authentication https://docs.microsoft.com/en-us/azure/active-directory/develop/scenario-desktop-acquire-token-integrated-windows-authentication

            var account = await PublicClient.GetAccountAsync(AuthenticationResult?.Account.Identifier).ConfigureAwait(false);
            var scopes = _pbicloudSettings.CloudEnvironment.AzureADScopes;

            try
            {
                // Try to acquire an access token from the cache, if UI interaction is required, MsalUiRequiredException will be thrown.
                var msalAuthenticationResult = await PublicClient.AcquireTokenSilent(scopes, account).WithExtraQueryParameters(MicrosoftAccountOnlyQueryParameter).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                var pbicloudAuthenticationResult = new PBICloudAuthenticationResult(msalAuthenticationResult);

                return pbicloudAuthenticationResult;

                //if ()
                //{
                //    AcquireTokenSilent(scopes, account)
                //}
                //else
                //{
                //    if (AppEnvironment.IsIntegratedWindowsAuthenticationSsoSupportEnabled)
                //    {
                //        ////var authenticationResult = await AcquireTokenByIntegratedWindowsAuth(scopes).ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
                //        ////// Assert UPN from local windows account is equals to UPN from authentication result
                //        ////BravoUnexpectedException.Assert(authenticationResult.ClaimsPrincipal.Identity.Name == account.Username);
                //    }
                //    else
                //    {
                //        ////AcquireTokenInteractive(scopes)
                //    }
                //}
            }
            catch (MsalUiRequiredException ex)
            {
                try
                {
                    // *** EmbeddedWebView requirements ***
                    // Requires VS project OutputType=WinExe and TargetFramework=net5-windows10.0.17763.0
                    // Using 'TargetFramework=net5-windows10.0.17763.0' the framework 'Microsoft.Windows.SDK.NET' is also included as project dependency.
                    // The framework 'Microsoft.Windows.SDK.NET' includes all the WPF(PresentationFramework.dll) and WinForm(System.Windows.Forms.dll) assemblies to the project.

                    var useEmbeddedBrowser = (UserPreferences.Current.Experimental?.UseSystemBrowserForAuthentication ?? false) == false;
                    var parameterBuilder = PublicClient.AcquireTokenInteractive(scopes)
                        .WithExtraQueryParameters(MicrosoftAccountOnlyQueryParameter)
                        .WithUseEmbeddedWebView(useEmbeddedBrowser)
                        .WithPrompt(Prompt.SelectAccount) // Force a sign-in (Prompt.SelectAccount), as the MSAL web browser might contain cookies for the current user and we don't necessarily want to re-sign-in the same user                         
                        .WithLoginHint(loginHint)
                        .WithClaims(ex.Claims);

                    if (useEmbeddedBrowser)
                    {
                        var mainwindowHwnd = ProcessHelper.GetCurrentProcessMainWindowHandle();
                        parameterBuilder.WithParentActivityOrWindow(mainwindowHwnd);
                    }

                    var msalAuthenticationResult = await parameterBuilder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    var pbicloudAuthenticationResult = new PBICloudAuthenticationResult(msalAuthenticationResult);

                    return pbicloudAuthenticationResult;
                }
                catch (MsalException) // ex.ErrorCode => Microsoft.Identity.Client.MsalError
                {
                    throw;
                }
            }
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (_publicClient is null)
            {
                await _pbicloudSettings.InitializeAsync(cancellationToken).ConfigureAwait(false);

                _publicClient = MsalHelper.CreatePublicClientApplication(_pbicloudSettings.CloudEnvironment);
            }
        }

        #region IDisposable

        public void Dispose()
        {
            _authenticationSemaphore.Dispose();
        }

        #endregion
    }
}
