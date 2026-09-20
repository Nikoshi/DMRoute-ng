namespace DMRoute_ng.Services;

public static class DmrServiceIds
{
    public const int First = 990000;
    public const int Last = 999999;
    public const int Echo = 990099;

    public static bool IsReserved(int id) => id is >= First and <= Last;
    public static bool IsFiveDigitRadio(int id) => id is >= 10000 and <= 99999;
}
