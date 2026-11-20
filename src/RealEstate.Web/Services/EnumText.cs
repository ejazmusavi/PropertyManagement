using System.Text;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace RealEstate.Web;

/// <summary>Readable labels for enum values in views ("SaleConsideration" becomes "Sale consideration", "HalfYearly" becomes "Half yearly").</summary>
public static class EnumText
{
    public static string Label(this Enum value)
    {
        var name = value.ToString();
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (i > 0 && char.IsUpper(ch) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                sb.Append(' ');
                ch = char.ToLowerInvariant(ch);
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Select options with the numeric value (same binding as <c>Html.GetEnumSelectList</c>) and a readable label.</summary>
    public static IEnumerable<SelectListItem> Options<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(v => new SelectListItem(v.Label(), Convert.ToInt64(v).ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
