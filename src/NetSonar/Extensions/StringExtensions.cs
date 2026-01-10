using System.IO;
using System.Text.RegularExpressions;
using Utf8StringInterpolation;

namespace NetSonar.Avalonia.Extensions;

public static partial class StringExtensions
{
    /// <param name="strInput">The string to parse</param>
    extension(string strInput)
    {
        /// <summary>
        /// Parse the input string by placing a space between character case changes in the string
        /// </summary>
        /// <param name="splitNumbers">Also split numbers with a whitespace</param>
        /// <returns>The altered string</returns>
        public string InsertSpaceBetweenCamelCase(bool splitNumbers = true)
        {
            if (string.IsNullOrWhiteSpace(strInput)) return strInput;
            using var writer = Utf8String.CreateWriter(out var zsb);
            // The altered string (with spaces between the case changes)

            // The index of the current character in the input string
            int intCurrentCharPos;

            // The index of the last character in the input string
            int intLastCharPos = strInput.Length - 1;

            // for every character in the input string
            for (intCurrentCharPos = 0; intCurrentCharPos <= intLastCharPos; intCurrentCharPos++)
            {
                // Get the current character from the input string
                char chrCurrentInputChar = strInput[intCurrentCharPos];

                // At first, set previous character to the current character in the input string
                char chrPreviousInputChar = chrCurrentInputChar;

                // If this is not the first character in the input string
                if (intCurrentCharPos > 0)
                {
                    // Get the previous character from the input string
                    chrPreviousInputChar = strInput[intCurrentCharPos - 1];

                } // end if

                // Put a space before each upper case character if the previous character is lower case
                if ((char.IsUpper(chrCurrentInputChar) && char.IsLower(chrPreviousInputChar))
                    || (splitNumbers && char.IsNumber(chrCurrentInputChar) && !char.IsNumber(chrPreviousInputChar) && intCurrentCharPos < intLastCharPos))
                {
                    // Add a space to the output string
                    zsb.Append(' ');
                } // end if

                // Add the character from the input string to the output string
                zsb.Append(chrCurrentInputChar);

            } // next

            zsb.Flush();
            // Return the altered string
            return writer.ToString();

        }
    } // end method

    public static string GetSafeFilename(string filename)
    {

        return string.Join("_", filename.Split(Path.GetInvalidFileNameChars()));

    }
    [GeneratedRegex(@"\r\n?|\n")]
    private static partial Regex LinebreakRegex();

    public static string ReplaceLinebreak(string str, string replacementStr)
    {
        return LinebreakRegex().Replace(str, replacementStr);
    }
}