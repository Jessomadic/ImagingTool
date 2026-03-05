using ImagingTool.Helpers;

namespace ImagingTool.Services
{
    public class VerifyService
    {
        private readonly AppSettings _settings;
        private readonly IProcessRunner _processRunner;

        public VerifyService(AppSettings settings, IProcessRunner processRunner)
        {
            _settings = settings;
            _processRunner = processRunner;
        }

        public async Task VerifyWimFile(string wimPath)
        {
            if (!File.Exists(wimPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: WIM file not found: {wimPath}");
                Console.ResetColor();
                throw new FileNotFoundException($"WIM file not found: {wimPath}");
            }

            Console.WriteLine("\n--- WIM Verification ---");
            Console.WriteLine($"Verifying image: {wimPath}");

            var args = $"verify \"{wimPath}\"";
            bool success = await _processRunner.RunProcessAsync(_settings.WimlibPath, args, "WimLib Verify");

            if (!success)
                throw new Exception($"WIM verification failed for: {wimPath}. The image may be corrupted.");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("WIM verification passed. Image integrity confirmed.");
            Console.ResetColor();
        }
    }
}
