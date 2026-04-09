using CMBuyerStudio.Application.Enums;

namespace CMBuyerStudio.Application.Enums;

public static class SellerScopeModeExtensions
{
    public static bool IsLocal(this SellerScopeMode scope)
        => scope == SellerScopeMode.Local;

    public static bool IsEu(this SellerScopeMode scope)
        => scope == SellerScopeMode.Eu;
}