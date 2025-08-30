using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Sympho.Models;

namespace Sympho.Functions
{
    public class CacheManager
    {
        private readonly ILogger<Sympho> _logger;
        private string _rootDir = "";
        private int _maxAgeMinutes;
        private int _maxCount;
        private bool _reuse;

        public CacheManager(ILogger<Sympho> logger)
        {
            _logger = logger;
        }

        public void Initialize(Sympho plugin)
        {
            var cfg = plugin.Config;
            // Use the directory from config, ensuring it's not empty.
            var tmpDir = string.IsNullOrWhiteSpace(cfg.TmpDir) ? "sympho_tmp" : cfg.TmpDir;
            _rootDir = Path.Combine(plugin.ModuleDirectory, tmpDir);
            Directory.CreateDirectory(_rootDir);

            _maxAgeMinutes = Math.Max(1, cfg.TmpMaxAgeMinutes);
            _maxCount = cfg.CacheMaxCount;
            _reuse = cfg.CacheReuseEnabled;
            
            _logger.LogInformation("[CacheManager] Initialized. Path: {path}, MaxAge: {age}min, MaxCount: {count}", _rootDir, _maxAgeMinutes, _maxCount);
        }

        public string GetPathForUrl(string url)
        {
            var videoId = Youtube.GetYouTubeVideoId(url);
            if (!string.IsNullOrEmpty(videoId))
            {
                var name = $"{videoId}.mp3";
                return Path.Combine(_rootDir!, name);
            }
            
            // Fallback for non-youtube URLs
            var id = StableId(url);
            var hashName = $"{id}.mp3";
            return Path.Combine(_rootDir!, hashName);
        }

        /// <summary>
        /// Checks if a valid, non-expired cached file exists for the given URL.
        /// </summary>
        /// <returns>True if a valid cache file is found.</returns>
        public bool TryGetValid(string url, out string path)
        {
            path = GetPathForUrl(url);
            if (!_reuse) return false;
            if (!File.Exists(path))
            {
                 _logger.LogInformation("[CacheManager] Cache MISS for URL {url} (file not found)", url);
                return false;
            }

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age.TotalMinutes <= _maxAgeMinutes)
            {
                _logger.LogInformation("[CacheManager] Cache HIT for URL {url}", url);
                return true;
            }
            
            _logger.LogInformation("[CacheManager] Cache MISS for URL {url} (file expired)", url);
            return false;
        }

        /// <summary>
        /// Updates the file's last write time to keep it in the cache longer.
        /// Also triggers a cleanup if cache is over capacity.
        /// </summary>
        public void Touch(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    CleanupOverCapacity();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CacheManager] Failed to touch file: {path}", path);
            }
        }

        /// <summary>
        /// Deletes all files in the cache directory that are older than the configured max age.
        /// </summary>
        public void CleanupExpired()
        {
            if(string.IsNullOrEmpty(_rootDir)) return;
            _logger.LogInformation("[CacheManager] Running expired cache cleanup...");
            foreach (var f in SafeEnumFiles(_rootDir))
            {
                try
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(f);
                    if (age.TotalMinutes > _maxAgeMinutes)
                    {
                        File.Delete(f);
                        _logger.LogInformation("[CacheManager] Deleted expired file: {file}", f);
                    }
                }
                catch { /* ignore */ }
            }
        }

        /// <summary>
        /// If the number of cache files exceeds the configured limit, deletes the oldest ones.
        /// </summary>
        private void CleanupOverCapacity()
        {
            if (_maxCount <= 0 || string.IsNullOrEmpty(_rootDir)) return;

            var files = SafeEnumFiles(_rootDir)
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToList();

            if (files.Count <= _maxCount) return;

            _logger.LogInformation("[CacheManager] Cache capacity ({count}) exceeds limit ({limit}). Cleaning up...", files.Count, _maxCount);
            foreach (var fi in files.Skip(_maxCount))
            {
                try 
                { 
                    fi.Delete();
                    _logger.LogInformation("[CacheManager] Deleted over-capacity file: {file}", fi.FullName);
                } 
                catch { /* ignore */ }
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