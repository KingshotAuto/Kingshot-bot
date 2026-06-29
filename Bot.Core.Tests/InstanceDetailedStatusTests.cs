using System;
using Bot.Core.Models;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class InstanceDetailedStatusTests
    {
        [Test]
        public void FromList2Parts_ValidInput_ReturnsCorrectStatus()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "2032678", "1704928", "1", "7456", "3500" };

            // Act
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Assert
            Assert.IsNotNull(status);
            Assert.AreEqual(0, status.Index);
            Assert.AreEqual("LDPlayer", status.Title);
            Assert.AreEqual(2032678, status.TopWindowHandle);
            Assert.AreEqual(1704928, status.BindWindowHandle);
            Assert.IsTrue(status.AndroidStarted);
            Assert.AreEqual(7456, status.ProcessId);
            Assert.AreEqual(3500, status.VBoxProcessId);
            Assert.IsTrue(status.IsRunning);
            Assert.IsTrue(status.IsFullyBooted);
        }

        [Test]
        public void FromList2Parts_AndroidNotStarted_ReturnsCorrectStatus()
        {
            // Arrange
            var parts = new[] { "1", "LDPlayer-1", "852422", "590830", "0", "3772", "3180" };

            // Act
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Assert
            Assert.IsNotNull(status);
            Assert.AreEqual(1, status.Index);
            Assert.IsTrue(status.IsRunning); // Process is running
            Assert.IsFalse(status.AndroidStarted); // But Android not started
            Assert.IsFalse(status.IsFullyBooted); // Therefore not fully booted
            Assert.AreEqual("Emulator Running, Android Starting", status.StatusDescription);
        }

        [Test]
        public void FromList2Parts_NotRunning_ReturnsCorrectStatus()
        {
            // Arrange
            var parts = new[] { "2", "LDPlayer-2", "0", "0", "0", "0", "0" };

            // Act
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Assert
            Assert.IsNotNull(status);
            Assert.AreEqual(2, status.Index);
            Assert.IsFalse(status.IsRunning);
            Assert.IsFalse(status.AndroidStarted);
            Assert.IsFalse(status.IsFullyBooted);
            Assert.IsFalse(status.HasValidHandles);
            Assert.AreEqual("Not Running", status.StatusDescription);
        }

        [Test]
        public void FromList2Parts_InsufficientParts_ReturnsNull()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "123" }; // Only 3 parts, need 7

            // Act
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Assert
            Assert.IsNull(status);
        }

        [Test]
        public void FromList2Parts_InvalidData_ReturnsNull()
        {
            // Arrange
            var parts = new[] { "invalid", "LDPlayer", "not_a_number", "1704928", "1", "7456", "3500" };

            // Act
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Assert
            Assert.IsNull(status);
        }

        [Test]
        public void ReadinessLevel_NotRunning_ReturnsNotRunning()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "0", "0", "0", "0", "0" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Act
            var readiness = status.ReadinessLevel;

            // Assert
            Assert.AreEqual(InstanceReadiness.NotRunning, readiness);
        }

        [Test]
        public void ReadinessLevel_RunningButAndroidNotStarted_ReturnsBooting()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "1000", "2000", "0", "1234", "5678" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Act
            var readiness = status.ReadinessLevel;

            // Assert
            Assert.AreEqual(InstanceReadiness.Booting, readiness);
        }

        [Test]
        public void ReadinessLevel_AndroidStartedButNoHandles_ReturnsStarting()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "0", "0", "1", "1234", "5678" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Act
            var readiness = status.ReadinessLevel;

            // Assert
            Assert.AreEqual(InstanceReadiness.Starting, readiness);
        }

        [Test]
        public void ReadinessLevel_FullyBooted_ReturnsReady()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "1000", "2000", "1", "1234", "5678" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Act
            var readiness = status.ReadinessLevel;

            // Assert
            Assert.AreEqual(InstanceReadiness.Ready, readiness);
        }

        [Test]
        public void NeedsRecovery_RunningTooLongWithoutAndroid_ReturnsTrue()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "1000", "2000", "0", "1234", "5678" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);
            
            // Simulate old timestamp (6 minutes ago)
            status.LastUpdated = DateTime.UtcNow.AddMinutes(-6);

            // Act
            var needsRecovery = status.NeedsRecovery;

            // Assert
            Assert.IsTrue(needsRecovery);
        }

        [Test]
        public void NeedsRecovery_RecentUpdate_ReturnsFalse()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "1000", "2000", "0", "1234", "5678" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);
            
            // Recent update (1 minute ago)
            status.LastUpdated = DateTime.UtcNow.AddMinutes(-1);

            // Act
            var needsRecovery = status.NeedsRecovery;

            // Assert
            Assert.IsFalse(needsRecovery);
        }

        [Test]
        public void ToString_ReturnsFormattedString()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "2032678", "1704928", "1", "7456", "3500" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Act
            var stringValue = status.ToString();

            // Assert
            Assert.IsNotNull(stringValue);
            Assert.Contains("Instance 0", stringValue);
            Assert.Contains("LDPlayer", stringValue);
            Assert.Contains("Fully Ready", stringValue);
            Assert.Contains("PID: 7456", stringValue);
            Assert.Contains("VBox: 3500", stringValue);
        }
    }
}