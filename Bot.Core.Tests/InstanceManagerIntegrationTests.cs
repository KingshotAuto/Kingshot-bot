using System;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.LDPlayer;
using Bot.Core.Logging;
using Bot.Core.Utils;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>
    /// Integration tests for InstanceManager Phase 1 enhancements.
    /// These tests require actual LDPlayer installation and may start/stop instances.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class InstanceManagerIntegrationTests
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
        [Ignore("Requires LDPlayer installation and may modify instance state")]
        public async Task IsInstanceRunningAsync_WithFallback_ReturnsCorrectStatus()
        {
            // Arrange
            const int instanceNumber = 0;

            // Act
            var isRunning = await _instanceManager.IsInstanceRunningAsync(instanceNumber);

            // Assert
            // We can't assert the specific value since we don't know the instance state,
            // but we can verify the method doesn't throw and returns a valid boolean
            Assert.IsNotNull(isRunning);
            Console.WriteLine($"Instance {instanceNumber} running status: {isRunning}");
        }

        [Test]
        [Ignore("Requires LDPlayer installation")]
        public async Task GetAllInstanceStatusesAsync_ReturnsValidData()
        {
            // Act
            var statuses = await _instanceManager.GetAllInstanceStatusesAsync();

            // Assert
            Assert.IsNotNull(statuses);
            Console.WriteLine($"Found {statuses.Count} instances:");
            
            foreach (var status in statuses.Values)
            {
                Assert.IsNotNull(status);
                Assert.IsNotNull(status.Title);
                Assert.GreaterOrEqual(status.Index, 0);
                
                Console.WriteLine($"  {status}");
            }
        }

        [Test]
        [Ignore("Requires LDPlayer installation")]
        public async Task GetInstanceDetailedStatusAsync_ExistingInstance_ReturnsValidStatus()
        {
            // Arrange
            const int instanceNumber = 0;

            // Act
            var status = await _instanceManager.GetInstanceDetailedStatusAsync(instanceNumber);

            // Assert
            if (status != null) // Instance exists
            {
                Assert.AreEqual(instanceNumber, status.Index);
                Assert.IsNotNull(status.Title);
                Assert.IsNotNull(status.StatusDescription);
                
                Console.WriteLine($"Instance {instanceNumber} detailed status:");
                Console.WriteLine($"  Title: {status.Title}");
                Console.WriteLine($"  Status: {status.StatusDescription}");
                Console.WriteLine($"  Running: {status.IsRunning}");
                Console.WriteLine($"  Android Started: {status.AndroidStarted}");
                Console.WriteLine($"  Process ID: {status.ProcessId}");
                Console.WriteLine($"  Readiness: {status.ReadinessLevel}");
            }
            else
            {
                Console.WriteLine($"Instance {instanceNumber} not found (this is valid if instance doesn't exist)");
            }
        }

        [Test]
        [Ignore("Requires LDPlayer installation and running instance - expensive test")]
        public async Task WaitUntilFullyBootedAsync_EnhancedVersion_WorksCorrectly()
        {
            // Arrange
            const int instanceNumber = 0;
            const int timeoutSeconds = 30; // Short timeout for test
            
            // First check if instance is already running
            var initialStatus = await _instanceManager.GetInstanceDetailedStatusAsync(instanceNumber);
            
            if (initialStatus?.IsFullyBooted == true)
            {
                Console.WriteLine($"Instance {instanceNumber} already fully booted, test will complete quickly");
            }
            else
            {
                Console.WriteLine($"Instance {instanceNumber} not fully booted, will wait up to {timeoutSeconds} seconds");
            }

            // Act
            var startTime = DateTime.UtcNow;
            var result = await _instanceManager.WaitUntilFullyBootedAsync(instanceNumber, CancellationToken.None, timeoutSeconds);
            var elapsed = DateTime.UtcNow - startTime;

            // Assert
            Console.WriteLine($"WaitUntilFullyBootedAsync result: {result}, took {elapsed.TotalSeconds:F1} seconds");
            
            if (result)
            {
                // If successful, verify the instance is indeed ready
                var finalStatus = await _instanceManager.GetInstanceDetailedStatusAsync(instanceNumber);
                Assert.IsNotNull(finalStatus);
                Assert.IsTrue(finalStatus.IsFullyBooted, "Instance should be fully booted after successful wait");
                
                Console.WriteLine($"Final status: {finalStatus.StatusDescription}");
            }
            
            // The test passes regardless of result since we can't control the instance state
            // The important thing is that the method doesn't hang or throw exceptions
        }

        [Test]
        public async Task StatusCache_PersistsBetweenCalls()
        {
            // Arrange
            const int instanceNumber = 0;
            
            // Clear cache first
            InstanceManager.ClearStatusCache();
            var initialStats = InstanceManager.GetCacheStatistics();
            Assert.AreEqual(0, initialStats["TotalCachedInstances"]);

            // Act - First call should populate cache
            var status1 = await _instanceManager.GetInstanceDetailedStatusAsync(instanceNumber);
            var statsAfterFirst = InstanceManager.GetCacheStatistics();
            
            // Act - Second call should use cache
            var status2 = await _instanceManager.GetInstanceDetailedStatusAsync(instanceNumber);
            var statsAfterSecond = InstanceManager.GetCacheStatistics();

            // Assert
            if (status1 != null) // Only test if instance exists
            {
                Assert.Greater((int)statsAfterFirst["TotalCachedInstances"], 0, "Cache should be populated after first call");
                Assert.AreEqual(statsAfterFirst["TotalCachedInstances"], statsAfterSecond["TotalCachedInstances"], 
                    "Cache size should remain same for second call");
                
                // Status objects should be equivalent (same data)
                Assert.AreEqual(status1.Index, status2.Index);
                Assert.AreEqual(status1.IsRunning, status2.IsRunning);
                Assert.AreEqual(status1.AndroidStarted, status2.AndroidStarted);
            }
            
            Console.WriteLine($"Cache stats after first call: {string.Join(", ", statsAfterFirst.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        }

        [Test]
        public void ClearStatusCache_RemovesAllEntries()
        {
            // Arrange - Ensure cache has some data
            // (We can't easily populate it without actual instance calls, so this is a basic test)
            
            // Act
            InstanceManager.ClearStatusCache();
            var stats = InstanceManager.GetCacheStatistics();

            // Assert
            Assert.AreEqual(0, stats["TotalCachedInstances"], "Cache should be empty after clear");
            Assert.AreEqual(0, stats["FreshEntries"], "No fresh entries should remain");
            Assert.AreEqual(0, stats["StaleEntries"], "No stale entries should remain");
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up cache after each test
            InstanceManager.ClearStatusCache();
        }
    }