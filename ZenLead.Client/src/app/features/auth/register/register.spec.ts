import { Register } from './register';

describe('Register.messageFor', () => {
  it('explains a duplicate email on 409', () => {
    expect(Register.messageFor({ status: 409 })).toContain('already registered');
  });

  it('surfaces server validation messages on 400', () => {
    const message = Register.messageFor({
      status: 400,
      error: { errors: { Password: ['Password must contain a digit.', 'Password must contain an uppercase letter.'] } }
    });
    expect(message).toBe('Password must contain a digit. Password must contain an uppercase letter.');
  });

  it('falls back to a generic message otherwise', () => {
    expect(Register.messageFor({ status: 500 })).toBe('Registration failed. Please try again.');
  });
});
