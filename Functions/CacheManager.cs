using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Sympho.Models;
using Sympho.Functions;

namespace Sympho.Functions
{
    public class CacheManager
    {
        private string? _rootDir;
        private int _maxAgeMinutes;
        private int _maxCount;
        private bool _reuse;

        public CacheManager()
        {
            // 생성자는 비워둡니다.
        }

        public void Initialize(Sympho plugin)
        {
            _rootDir = Path.Combine(plugin.ModuleDirectory, "tmp");
            Directory.CreateDirectory(_rootDir!);

            var cfg = plugin.Config;
            _maxAgeMinutes = Math.Max(1, cfg.TmpMaxAgeMinutes);
            _maxCount = cfg.CacheMaxCount;
            _reuse = cfg.CacheReuseEnabled;
        }

        public string GetPathForUrl(string url)
        {
            var videoId = Youtube.GetYouTubeVideoId(url);
            if (!string.IsNullOrEmpty(videoId))
            {
                var name = $"{videoId}.mp3";
                return Path.Combine(_rootDir!, name);
            }
            
            var id = StableId(url);
            var hashName = $"{id}.mp3";
            return Path.Combine(_rootDir!, hashName);
        }

        public bool TryGetValid(string url, out string path)
        {
            path = GetPathForUrl(url);
            if (!_reuse) return false;
            if (!File.Exists(path)) return false;

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            return age.TotalMinutes <= _maxAgeMinutes;
        }

        public void Touch(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                CleanupOverCapacity();
            }
            catch { /* ignore */ }
        }

        public void CleanupExpired()
        {
            if(string.IsNullOrEmpty(_rootDir)) return;
            foreach (var f in SafeEnumFiles(_rootDir))
            {
                try
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(f);
                    if (age.TotalMinutes > _maxAgeMinutes)
                        File.Delete(f);
                }
                catch { /* ignore */ }
            }
        }

        private void CleanupOverCapacity()
        {
            if (_maxCount <= 0 || string.IsNullOrEmpty(_rootDir)) return;

            var files = SafeEnumFiles(_rootDir)
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToList();

            if (files.Count <= _maxCount) return;

            foreach (var fi in files.Skip(_maxCount))
            {
                try { fi.Delete(); } catch { /* ignore */ }
            }
        }

        private static IEnumerable<string> SafeEnumFiles(string dir)
        {
            try { return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly); }
            catch { return Array.Empty<string>(); }
        }

        private static string StableId(string url)
        {
            using var sha1 = SHA1.Create();
            var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(url.Trim()));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}