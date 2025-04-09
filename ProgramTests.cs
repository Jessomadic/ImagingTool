using System;
using System.IO;
using Xunit;
using Moq;

namespace ImagingTool.Tests
{
    public class ProgramTests
    {
        [Fact]
        public void IsCloudOnly_ShouldReturnTrue_WhenFileDoesNotExist()
        {
            // Arrange
            var nonExistentFilePath = @"C:\NonExistentFile.txt";

            // Act
            var result = Program.IsCloudOnly(nonExistentFilePath);

            // Assert
            Assert.True(result, "Expected IsCloudOnly to return true for non-existent files.");
        }

        [Fact]
        public void IsCloudOnly_ShouldReturnTrue_WhenFileHasRecallOnOpenAttribute()
        {
            // Arrange
            var mockFilePath = @"C:\MockFile.txt";
            File.Create(mockFilePath).Dispose();
            File.SetAttributes(mockFilePath, FileAttributes.Offline | (FileAttributes)0x400000); // Simulate RECALL_ON_OPEN

            try
            {
                // Act
                var result = Program.IsCloudOnly(mockFilePath);

                // Assert
                Assert.True(result, "Expected IsCloudOnly to return true for files with RECALL_ON_OPEN attribute.");
            }
            finally
            {
                // Cleanup
                File.Delete(mockFilePath);
            }
        }

        [Fact]
        public void IsCloudOnly_ShouldReturnFalse_WhenFileIsLocal()
        {
            // Arrange
            var mockFilePath = @"C:\MockFile.txt";
            File.Create(mockFilePath).Dispose();

            try
            {
                // Act
                var result = Program.IsCloudOnly(mockFilePath);

                // Assert
                Assert.False(result, "Expected IsCloudOnly to return false for local files.");
            }
            finally
            {
                // Cleanup
                File.Delete(mockFilePath);
            }
        }

        [Fact]
        public void ExtractFilePathFromError_ShouldReturnFilePath_WhenErrorMessageContainsQuotedPath()
        {
            // Arrange
            var errorMessage = "Error: Access is denied to \"C:\\MockFile.txt\".";

            // Act
            var result = Program.ExtractFilePathFromError(errorMessage);

            // Assert
            Assert.Equal(@"C:\MockFile.txt", result);
        }

        [Fact]
        public void ExtractFilePathFromError_ShouldReturnEmptyString_WhenErrorMessageDoesNotContainQuotedPath()
        {
            // Arrange
            var errorMessage = "Error: Access is denied.";

            // Act
            var result = Program.ExtractFilePathFromError(errorMessage);

            // Assert
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public async Task RetryBackupForFile_ShouldSkipCloudOnlyFiles()
        {
            // Arrange
            var mockFilePath = @"C:\MockCloudFile.txt";
            var mockDestination = @"C:\Backup.wim";

            // Mock IsCloudOnly to return true
            var programMock = new Mock<Program>();
            programMock.Setup(p => Program.IsCloudOnly(mockFilePath)).Returns(true);

            // Act
            await Program.RetryBackupForFile(mockFilePath, mockDestination);

            // Assert
            programMock.Verify(p => Program.IsCloudOnly(mockFilePath), Times.Once);
        }

        [Fact]
        public async Task RetryBackupForFile_ShouldRetryUpToMaxAttempts_WhenFileIsLocal()
        {
            // Arrange
            var mockFilePath = @"C:\MockLocalFile.txt";
            var mockDestination = @"C:\Backup.wim";

            // Mock IsCloudOnly to return false
            var programMock = new Mock<Program>();
            programMock.Setup(p => Program.IsCloudOnly(mockFilePath)).Returns(false);

            // Act
            await Program.RetryBackupForFile(mockFilePath, mockDestination, maxAttempts: 3);

            // Assert
            programMock.Verify(p => Program.IsCloudOnly(mockFilePath), Times.Once);
        }
    }
}
