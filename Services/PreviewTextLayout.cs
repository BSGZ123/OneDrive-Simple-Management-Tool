namespace OneDrive_Simple_Management_Tool.Services
{
    public static class PreviewTextLayout
    {
        // Bound the amount of synchronous work handed to the legacy XAML Markdown renderer.
        public static bool UsePlainText(string text)
        {
            if (text.Length > 200_000) return true;
            int lines = 1, columns = 0, pipes = 0;
            foreach (char character in text)
            {
                if (character == '\n') { lines++; columns = 0; }
                else columns++;
                if (character == '|') pipes++;
                if (lines > 2000 || columns > 16000 || pipes > 4000) return true;
            }
            return false;
        }
    }
}
