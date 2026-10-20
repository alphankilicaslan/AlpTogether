using System;
using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace AlpTogether.Services
{
    /// <summary>
    /// Thread-safe, WPF bellek sızıntısını ve her 3 saniyede tekrarlı BitmapImage üretimini engelleyen dondurulmuş (frozen) bitmap önbelleği.
    /// WPF GPU texture önbelleğini paylaşarak RAM tüketimini minimumda tutar.
    /// </summary>
    public static class BitmapCacheHelper
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);

        public static BitmapImage? GetFrozenBitmap(string? uriString)
        {
            if (string.IsNullOrWhiteSpace(uriString))
                return null;

            return _cache.GetOrAdd(uriString, uri =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(uri, UriKind.RelativeOrAbsolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                catch
                {
                    if (uri.StartsWith("pack://application:,,,/Assets/", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string altUri = uri.Replace("pack://application:,,,/Assets/", "pack://application:,,,/AlpTogether;component/Assets/");
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(altUri, UriKind.RelativeOrAbsolute);
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                            bmp.EndInit();
                            bmp.Freeze();
                            return bmp;
                        }
                        catch { }
                    }
                    return null!;
                }
            });
        }

        public static void Clear()
        {
            _cache.Clear();
        }
    }
}
