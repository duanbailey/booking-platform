using BookingPlatform.Api.Auth;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T26 review: the worker writes invite and recovery link hashes that the API checks, so both must hash a token the
/// same way (lowercase hex SHA-256 of its UTF-8 bytes, specs/05-data-model.md). The worker's links.test.ts asserts
/// the same value for its tokenHash; change both together or every link stops working.
/// </summary>
public class TokenHashTests
{
    [Fact]
    public void Hashes_a_token_as_lowercase_hex_sha256_shared_with_the_worker()
    {
        Assert.Equal("4d1566a1d7df42a8517456d60ea06ed284e535cfe4c956aa6ee172dbcdf945f7", TokenHash.Of("example-token"));
    }
}
