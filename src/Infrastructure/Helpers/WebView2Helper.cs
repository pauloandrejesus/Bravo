namespace Sqlbi.Bravo.Infrastructure.Helpers
{
    using Sqlbi.Bravo.Infrastructure.Configuration.Settings;
    using Sqlbi.Bravo.Infrastructure.Extensions;
    using Sqlbi.Bravo.Infrastructure.Services;
    using Sqlbi.Bravo.Infrastructure.Windows.Interop;
    using System;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Windows.Forms;

    internal static class WebView2Helper
    {
        [DllImport(ExternDll.WebView2Loader)]
        internal static extern int GetAvailableCoreWebView2BrowserVersionString([In][MarshalAs(UnmanagedType.LPWStr)] string? browserExecutableFolder, [MarshalAs(UnmanagedType.LPWStr)] ref string versionInfo);

        /// <summary>
        /// The Bootstrapper is a tiny installer that downloads the Evergreen Runtime matching device architecture and installs it locally.
        /// https://developer.microsoft.com/en-us/microsoft-edge/webview2/#download-section
        /// </summary>
        public static string EvergreenRuntimeBootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        public static string MicrosoftReferenceUrl = "https://developer.microsoft.com/en-us/microsoft-edge/webview2";

        /// <summary>
        /// Additional environment variables verified when WebView2Environment is created.
        /// If additional browser arguments is specified in environment variable or in the registry, it is appended to the corresponding values in CreateCoreWebView2EnvironmentWithOptions parameters.
        /// https://docs.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/webview2-idl
        /// </summary>
        private const string EnvironmentVariableAdditionalBrowserArguments= "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";

        public static string? GetRuntimeVersionInfo()
        {
            var versionInfo = (string?)null;
#pragma warning disable CS8601 // Possible null reference assignment.
            var errorCode = GetAvailableCoreWebView2BrowserVersionString(browserExecutableFolder: null, ref versionInfo);
#pragma warning restore CS8601 // Possible null reference assignment.
            if (errorCode == HRESULT.E_FILENOTFOUND)
            {
                // WebView2 runtime not found
                return null;
            }

            Marshal.ThrowExceptionForHR(errorCode);
            return versionInfo;
        }

        public static void SetWebView2CmdlineProxyArguments(ProxySettings? settings, HttpSystemProxy systemProxy)
        {
            // Command-line options for proxy settings
            // https://docs.microsoft.com/en-us/deployedge/edge-learnmore-cmdline-options-proxy-settings#command-line-options-for-proxy-settings

#pragma warning disable IDE0059 // Unnecessary assignment of a value
            var proxyArguments = "--no-proxy-server";
#pragma warning restore IDE0059 // Unnecessary assignment of a value

            if (settings is null)
            {
                proxyArguments = GetSystemProxyArguments(systemProxy);
            }
            else
            {
                proxyArguments = settings.ProxyType switch
                {
                    ProxyType.System when systemProxy.HttpProxyUris?.Length > 0 || systemProxy.HttpsProxyUris?.Length > 0 => GetSystemProxyArguments(systemProxy),
                    ProxyType.Custom when settings.Address is not null => GetCustomProxyArguments(settings),
                    _ => "--no-proxy-server",
                };
            }

            Environment.SetEnvironmentVariable(EnvironmentVariableAdditionalBrowserArguments, proxyArguments, EnvironmentVariableTarget.Process);

            static string GetSystemProxyArguments(HttpSystemProxy systemProxy)
            {
                var builder = new StringBuilder();
                {
                    builder.AppendFormat("--proxy-server=\"{0}\"", GetSystemProxyServerListValue(systemProxy));
                    builder.Append(' ');
                    builder.AppendFormat("--proxy-bypass-list=\"{0}\"", GetProxyBypassListValue(systemProxy.BypassList));
                }

                return builder.ToString();
            }

            static string GetSystemProxyServerListValue(HttpSystemProxy systemProxy)
            {
                var builder = new StringBuilder();
                {
                    if (systemProxy.HttpProxyUris?.Length > 0)
                    {
                        foreach (var uri in systemProxy.HttpProxyUris)
                            builder.AppendFormat("http={0};", uri.Authority);
                    }

                    if (systemProxy.HttpsProxyUris?.Length > 0)
                    {
                        foreach (var uri in systemProxy.HttpsProxyUris)
                            builder.AppendFormat("https={0};", uri.Authority);
                    }
                }

                return builder.ToString();
            }

            static string GetCustomProxyArguments(ProxySettings settings)
            {
                var builder = new StringBuilder();
                {
                    builder.AppendFormat("--proxy-server=\"{0}\"", settings.Address);
                    builder.Append(' ');
                    builder.AppendFormat("--proxy-bypass-list=\"{0}\"", GetProxyBypassListValue(settings.BypassList));
                }

                return builder.ToString();
            }

            static string GetProxyBypassListValue(string[]? bypassList)
            {
                var builder = new StringBuilder();
                {
                    if (bypassList?.Length > 0)
                    {
                        foreach (var uriString in bypassList)
                        {
                            // Do not apply this special proxy bypass rule which has the effect of subtracting the implicit loopback rules
                            if ("<-loopback>".EqualsI(uriString))
                                continue;

                            builder.AppendFormat("{0};", uriString);
                        }
                    }

                    //--
                    // We always include loopback addresses to avoid routing local WebAPI traffic through a web proxy
                    // We apply it even if this would not be necessary since the browser applies implicit bypass rules - see https://chromium.googlesource.com/chromium/src/+/HEAD/net/docs/proxy.md#implicit-bypass-rules
                    //--
                    // Keep these rules at the end of the string, this is because sorting can matter when using a subtractive rule, as the rules will be evaluated in a left to right order
                    // --
                    // IPv6 literals must not be bracketed
                    builder.AppendFormat("{0};{1};", IPAddress.Loopback, IPAddress.IPv6Loopback);
                }

                return builder.ToString();
            }
        }

        public static void EnsureRuntimeIsInstalled()
        {
            if (AppEnvironment.IsWebView2RuntimeInstalled)
                return;

            var appIcon = Icon.ExtractAssociatedIcon(AppEnvironment.ProcessPath);
            var icon = new TaskDialogIcon(appIcon!);

            var page = new TaskDialogPage()
            {
                Caption = AppEnvironment.ApplicationMainWindowTitle,
                Heading = @$"{ AppEnvironment.ApplicationMainWindowTitle } requires the Microsoft Edge WebView2 runtime which is not currently installed.

Choose an option to proceed with the installation:",
                Icon = icon,
                AllowCancel = false,
                Footnote = new TaskDialogFootnote()
                {
                    Text = $"For more details please refer to the following address:\r\n\r\n - { AppEnvironment.ApplicationWebsiteUrl }\r\n - { MicrosoftReferenceUrl }",
                },
                Buttons =
                {
                    new TaskDialogCommandLinkButton("&Automatic", "Download and install Microsoft Edge WebView2 runtime now")
                    {
                        Tag = 10
                    },
                    new TaskDialogCommandLinkButton("&Manual", "Open the browser on the download page")
                    {
                        Tag = 20
                    },
                    new TaskDialogCommandLinkButton("&Cancel", "Close the application without installing")
                    {
                        Tag = 30,
                    }
                },
                //Expander = new TaskDialogExpander()
                //{
                //    Text = " ... ",
                //    Position = TaskDialogExpanderPosition.AfterFootnote
                //}
            };

            var dialogButton = TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);

            switch (dialogButton.Tag)
            {
                case 10:
                    DownloadAndInstallRuntime();
                    break;
                case 20:
                    _ = ProcessHelper.OpenInBrowser(new Uri(MicrosoftReferenceUrl, uriKind: UriKind.Absolute));
                    break;
                case 30:
                    // default to Environment.Exit
                    break;
                default:
                    throw new BravoUnexpectedException($"TaskDialog result '{ dialogButton.Tag }'");
            }

            Environment.Exit(NativeMethods.NO_ERROR);
        }

        private static void DownloadAndInstallRuntime()
        {
            // TODO: use http client from pool, add proxy support
            using var httpClient = new HttpClient();

            var fileBytes = httpClient.GetByteArrayAsync(EvergreenRuntimeBootstrapperUrl).GetAwaiter().GetResult();
            var filePath = Path.Combine(AppEnvironment.ApplicationTempPath, $"MicrosoftEdgeWebview2Setup-{DateTime.Now:yyyyMMddHHmmss}.exe");

            File.WriteAllBytes(filePath, fileBytes);

            using var process = Process.Start(filePath); // add switches ? i.e. /silent /install
            process.WaitForExit();

            if (process.ExitCode != NativeMethods.NO_ERROR)
            {
                ExceptionHelper.WriteToEventLog($"WebView2 bootstrapper exit code '{ process.ExitCode }'", EventLogEntryType.Warning);
            }
        }
    }
}
