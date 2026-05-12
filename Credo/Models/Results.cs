namespace Credo.Models
{
    public class Results
    {
        public int Records { get; set; }
        public bool Success { get; set; }
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
    }
}
