using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Bot.Core.LDPlayer;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Utils;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>
    /// Performance tests to validate Phase 1 improvements.
    /// These tests require a running LDPlayer instance to be meaningful.
    /// </summary>
    [TestFixture]
    [Category("Performance")]
    public class InstanceManagerPerformanceTests
    {
        private InstanceManager _instanceManager;
        private LogService _logger;

        [SetUp]
        public void Setup()
        {
            _logger = new LogService(0);
            var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
            var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
            _instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, _logger);
        }

        [Test]
        [Ignore("Requires running LDPlayer instance")]
        public async Task GetInstanceDetailedStatus_Performance_Under500ms()
        {
            // Arrange
            const int instanceNumber = 0;
            var stopwatch = new Stopwatch();

            // Act
            stopwatch.Start();
            var status = await _instanceManager.GetInstanceDetailedStatusAsync(instanceNumber);
            stopwatch.Stop();

            // Assert
            Assert.IsNotNull(status, "Status should not be null if instance exists");
            Assert.Less(stopwatch.ElapsedMilliseconds, 500, 
                $"Fast status check should complete in under 500ms, took {stopwatch.ElapsedMilliseconds}ms");
            
            Console.WriteLine($"Fast status check completed in {stopwatch.ElapsedMilliseconds}ms");
        }

        [Test]
        [Ignore("Requires running LDPlayer instance")]
        public async Task GetAllInstanceStatuses_Performance_Under1Second()
        {
            // Arrange
            var stopwatch = new Stopwatch();

            // Act
            stopwatch.Start();
            var statuses = await _instanceManager.GetAllInstanceStatusesAsync();
            stopwatch.Stop();

            // Assert
            Assert.IsNotNull(statuses);
            Assert.Less(stopwatch.ElapsedMilliseconds, 1000, 
                $"Batch status check should complete in under 1 second, took {stopwatch.ElapsedMilliseconds}ms");
            
            Console.WriteLine($"Batch status check for {statuses.Count} instances completed in {stopwatch.ElapsedMilliseconds}ms");
        }

        [Test]
        [Ignore("Requires running LDPlayer instance")]
        public async Task StatusCache_Performance_Under10ms()
        {
            // Arrange
            const int instanceNumber = 0;
            
            // Prime the cache
            await _instanceManager.GetInstanceDetailedStatusAsync(instanceNumber);
            
            var stopwatch = new Stopwatch();

            // Act - Second call should be cached
            stopwatch.Start();
            var status = await _instanceManager.GetInstanceDetailedStatusAsync(instanceNumber);
            stopwatch.Stop();

            // Assert
            Assert.IsNotNull(status);
            Assert.Less(stopwatch.ElapsedMilliseconds, 10, 
                $"Cached status check should complete in under 10ms, took {stopwatch.ElapsedMilliseconds}ms");
            
            Console.WriteLine($"Cached status check completed in {stopwatch.ElapsedMilliseconds}ms");
        }

        [Test]
        [Ignore("Requires running LDPlayer instance - expensive test")]
        public async Task CompareOldVsNewStatusCheck_Performance()
        {
            // Arrange
            const int instanceNumber = 0;
            const int iterations = 10;
            var legacyTimes = new List<long>();
            var fastTimes = new List<long>();
            
            // Clear cache to ensure fair comparison
            InstanceManager.ClearStatusCache();

            // Test legacy method
            for (int i = 0; i < iterations; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                var isRunning = await _instanceManager.IsInstanceRunningAsync(instanceNumber);
                stopwatch.Stop();
                legacyTimes.Add(stopwatch.ElapsedMilliseconds);
                
                // Add delay between tests
                await Task.Delay(100);
            }

            // Clear cache again
            InstanceManager.ClearStatusCache();

            // Test new fast method
            for (int i = 0; i < iterations; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                var status = await _instanceManager.GetInstanceDetailedStatusAsync(instanceNumber);
                stopwatch.Stop();
                fastTimes.Add(stopwatch.ElapsedMilliseconds);
                
                // Add delay between tests
                await Task.Delay(100);
            }

            // Calculate averages
            var avgLegacy = legacyTimes.Sum() / (double)legacyTimes.Count;
            var avgFast = fastTimes.Sum() / (double)fastTimes.Count;
            var improvementPercent = ((avgLegacy - avgFast) / avgLegacy) * 100;

            // Assert - should be at least 50% faster
            Assert.Greater(improvementPercent, 50, 
                $"Fast method should be at least 50% faster. Legacy: {avgLegacy:F1}ms, Fast: {avgFast:F1}ms, Improvement: {improvementPercent:F1}%");

            Console.WriteLine($"Performance comparison over {iterations} iterations:");
            Console.WriteLine($"Legacy method average: {avgLegacy:F1}ms");
            Console.WriteLine($"Fast method average: {avgFast:F1}ms");
            Console.WriteLine($"Improvement: {improvementPercent:F1}%");
        }

        [Test]
        public void ParseList2Output_Performance_ValidData()
        {
            // Arrange
            var sampleOutput = "0,LDPlayer,2032678,1704928,1,7456,3500\n" +
                              "1,LDPlayer-1,852422,590830,0,3772,3180\n" +
                              "2,LDPlayer-2,0,0,0,0,0\n" +
                              "3,LDPlayer-3,1234567,7654321,1,9999,8888";
            
            var stopwatch = new Stopwatch();

            // Act
            stopwatch.Start();
            // Access private method via reflection for testing
            var parseMethod = typeof(InstanceManager).GetMethod("ParseList2Output", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var result = (Dictionary<int, InstanceDetailedStatus>)parseMethod.Invoke(_instanceManager, new object[] { sampleOutput });
            stopwatch.Stop();

            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(4, result.Count);
            Assert.Less(stopwatch.ElapsedMilliseconds, 10, 
                $"Parsing should be very fast, took {stopwatch.ElapsedMilliseconds}ms");
            
            Console.WriteLine($"Parsed {result.Count} instances in {stopwatch.ElapsedMilliseconds}ms");
        }

        [Test]
        public void CacheStatistics_ReturnsValidData()
        {
            // Arrange & Act
            var stats = InstanceManager.GetCacheStatistics();

            // Assert
            Assert.IsNotNull(stats);
            Assert.IsTrue(stats.ContainsKey("TotalCachedInstances"));
            Assert.IsTrue(stats.ContainsKey("FreshEntries"));
            Assert.IsTrue(stats.ContainsKey("StaleEntries"));
            Assert.IsTrue(stats.ContainsKey("CacheTTL"));
            
            Console.WriteLine($"Cache statistics: {string.Join(", ", stats.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up cache after each test
            InstanceManager.ClearStatusCache();
        }
    }
}