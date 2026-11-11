using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ParsGate.Services
{
    public static class AudioAndSpeechService
    {
        public static void PlayConnectTone()
        {
            Task.Run(() =>
            {
                try
                {
                    Console.Beep(600, 120);
                    Console.Beep(900, 150);
                }
                catch { }
            });
        }

        public static void PlayDisconnectTone()
        {
            Task.Run(() =>
            {
                try
                {
                    Console.Beep(800, 120);
                    Console.Beep(500, 150);
                }
                catch { }
            });
        }

        public static void PlayErrorTone()
        {
            Task.Run(() =>
            {
                try
                {
                    Console.Beep(300, 250);
                }
                catch { }
            });
        }

        public static void AnnounceToNvda(TextBlock liveStatusTextBlock, string message)
        {
            if (liveStatusTextBlock == null || string.IsNullOrWhiteSpace(message))
                return;

            liveStatusTextBlock.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                try
                {
                    liveStatusTextBlock.Text = message;

                    var peer = UIElementAutomationPeer.FromElement(liveStatusTextBlock);
                    if (peer != null)
                    {
                        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                    }
                }
                catch { }
            }));
        }
    }
}