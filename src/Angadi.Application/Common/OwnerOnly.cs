namespace Angadi.Application.Common;

/// <summary>
/// V2S: marks a request only the shop owner may run once an owner PIN is set. The dispatcher refuses it with
/// OWNER_PIN_REQUIRED unless the owner unlocked in the last few minutes, so a screen that forgets to ask for the PIN
/// still cannot let staff do it. Shops without a PIN are unaffected. Requests that are owner-only only sometimes
/// (a discount on a bill, shop details other than the language, stock moves other than a purchase) check in their
/// handler instead.
/// </summary>
public interface IOwnerOnly { }
