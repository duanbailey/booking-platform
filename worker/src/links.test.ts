import { tokenHash } from './links';

// T26 review: the API checks the hashes the worker writes, so both must hash a token the same way (lowercase hex
// SHA-256 of its UTF-8 bytes, specs/05-data-model.md). The API's TokenHashTests asserts the same value for
// TokenHash.Of; change both together or every invite and recovery link stops working.
describe('token hash', () => {
  it('is lowercase hex SHA-256, the same value as the API\'s TokenHash.Of', () => {
    expect(tokenHash('example-token')).toBe('4d1566a1d7df42a8517456d60ea06ed284e535cfe4c956aa6ee172dbcdf945f7');
  });
});
