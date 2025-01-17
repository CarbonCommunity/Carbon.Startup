using System.Text.RegularExpressions;

namespace Doorstop.Utility;

internal static class Blacklist
{
	private static readonly string[] Items =
	{
		@"^HiddenValueBase$",
		@"^HiddenValue`1$",
		@"^Pool$"
	};

	internal static bool IsBlacklisted(string name)
	{
		foreach (string item in Items)
		{
			if (Regex.IsMatch(name, item))
			{
				return true;
			}
		}
		return false;
	}
}
