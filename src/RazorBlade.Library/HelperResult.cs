using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace RazorBlade;

/// <summary>
/// Used for defered execution (mainly for delegate-based helpers).
/// </summary>
public class HelperResult : IEncodedContent
{
    private readonly Func<TextWriter, Task> _writeAction;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="asyncAction"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public HelperResult(Func<TextWriter, Task> asyncAction)
    {
        _writeAction = asyncAction ?? throw new ArgumentNullException(nameof(asyncAction));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="writer"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public virtual void WriteTo(TextWriter writer)
    {
        if (writer == null)
            throw new ArgumentNullException(nameof(writer));

        _writeAction(writer).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        _writeAction(writer).GetAwaiter().GetResult();
        return writer.ToString();
    }
}
