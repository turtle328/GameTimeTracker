using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace GameTimeTracker
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = @"Local\GameTimeTracker_12d4aa61_5533_49b9_b71a_4ae6ee84a61d";

        private static Mutex? _singleInstanceMutex;
        private static bool _ownsSingleInstanceMutex;

        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            if (!TryAcquireSingleInstanceMutex())
            {
                Environment.Exit(0);
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseSingleInstanceMutex();
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            MainWindow window = new();
            _window = window;
            window.ActivateForLaunch(WasStartedByStartupTask());
        }

        private static bool WasStartedByStartupTask()
        {
            try
            {
                AppActivationArguments activationArguments = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
                return activationArguments.Kind == ExtendedActivationKind.StartupTask;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryAcquireSingleInstanceMutex()
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);

            try
            {
                _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                _ownsSingleInstanceMutex = true;
            }

            if (_ownsSingleInstanceMutex)
            {
                return true;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }

        private static void ReleaseSingleInstanceMutex()
        {
            if (!_ownsSingleInstanceMutex)
            {
                return;
            }

            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            finally
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
                _ownsSingleInstanceMutex = false;
            }
        }
    }
}
