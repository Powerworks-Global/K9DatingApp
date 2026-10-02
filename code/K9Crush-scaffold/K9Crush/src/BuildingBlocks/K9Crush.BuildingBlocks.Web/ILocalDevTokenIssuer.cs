namespace K9Crush.BuildingBlocks.Web;

/// <summary>
/// Port for minting the local Development-only bearer token used by
/// Auth:Provider=Local (see Identity's Commands/DevSignIn). The concrete
/// LocalDevTokenIssuer lives alongside this interface so it stays
/// unit-testable; Identity.Api (which owns the dev sign-in endpoint) depends
/// on this interface rather than on the JWT packages directly.
///
/// This is a development convenience, not a credential store: it produces
/// a token for an already-provisioned owner id, with no password check.
/// Api.Host refuses to start with Auth:Provider=Local outside Development,
/// and this issuer throws if its signing key isn't configured, so it can
/// never mint a token in any real environment.
/// </summary>
public interface ILocalDevTokenIssuer
{
    string IssueToken(Guid ownerId, string email);
}
