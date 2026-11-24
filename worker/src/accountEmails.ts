// Invite and account emails (T26): handlers for AccessGranted, PasswordResetRequested and PasswordChanged
// (specs/06-events.md "Worker handling"). Decisions 2026-11-24 (plan/tasks.md, T26): a link that is used or no longer
// the user's newest is skipped before claiming the notifications row; otherwise the token is made only when the email
// is to be sent (not for a Sent row), so a Pending or Failed retry replaces it and only the latest link works; links
// are APP_BASE_URL/accept-invite?token= and /reset-password?token=; APP_NAME names the product; expiries and change
// times are shown in the recipient's own zone.
import type { Contact } from './contacts';
import { emailBody } from './bookingEmails';
import type { BookingDirectory } from './directory';
import type { LinkStore, LinkTable } from './links';
import type { NotificationKind } from './notifications';
import type { EventHandler, HandlerContext } from './pipeline';
import { formatInstant } from './time';

export interface AppSettings {
  /** Absolute, without a trailing slash. */
  baseUrl: string;
  name: string;
}

export interface AccountEmailDeps {
  directory: Pick<BookingDirectory, 'business'>;
  links: Pick<LinkStore, 'skipReason' | 'issueToken'>;
  app: AppSettings;
  now: () => Date;
}

const INVITE_VALID_MS = 7 * 24 * 3600_000;
const RESET_VALID_MS = 3600_000;

export function accountHandlers(deps: AccountEmailDeps): Map<string, EventHandler> {
  const { app } = deps;

  /** Sends one link email unless the link could never work; the token is made inside compose (after the claim). */
  async function sendLink(
    ctx: HandlerContext, table: LinkTable, linkId: string, recipientUserId: string, kind: NotificationKind,
    validForMs: number, path: string, compose: (to: Contact, link: string, expires: string) => { subject: string; text: string },
  ): Promise<void> {
    const skip = await deps.links.skipReason(table, linkId);
    if (skip) {
      ctx.log.info('Link skipped', { reason: skip, linkId, kind });
      return;
    }
    await ctx.sendEmail(recipientUserId, kind, async (to) => {
      const { token, expiresAt } = await deps.links.issueToken(table, linkId, deps.now(), validForMs);
      return compose(to, `${app.baseUrl}/${path}?token=${token}`, formatInstant(expiresAt.toISOString(), to.timeZone));
    });
  }

  const accessGranted: EventHandler = async (ctx) => {
    const data = ctx.event.data as { userId: string; businessId: string; role: 'Owner' | 'Staff'; inviteId: string | null };
    const business = await deps.directory.business(data.businessId);
    const granted = data.role === 'Owner'
      ? `You are now the owner of ${business.name} on ${app.name}.`
      : `You have been added as staff at ${business.name} on ${app.name}.`;

    if (data.inviteId === null) {
      await ctx.sendEmail(data.userId, 'AccessGranted', (to) => ({
        subject: data.role === 'Owner'
          ? `${app.name}: you now own ${business.name}`
          : `${app.name}: you have been added as staff at ${business.name}`,
        text: emailBody(to, `${granted} Log in with your existing account:`, [app.baseUrl]),
      }));
      return;
    }

    // An owner invite reads differently from a staff invite (plan/tasks.md T26 note).
    await sendLink(ctx, 'user_invites', data.inviteId, data.userId, 'Invite', INVITE_VALID_MS, 'accept-invite', (to, link, expires) => ({
      subject: data.role === 'Owner'
        ? `${app.name}: set your password to manage ${business.name}`
        : `${app.name}: set your password to join ${business.name}`,
      text: emailBody(to, `${granted} Set your password to get started:`, [
        link,
        '',
        `This link works once and expires on ${expires}.`,
        'If it has expired, ask for a new invite.',
      ]),
    }));
  };

  const passwordResetRequested: EventHandler = async (ctx) => {
    const data = ctx.event.data as { userId: string; passwordResetId: string };
    await sendLink(ctx, 'password_resets', data.passwordResetId, data.userId, 'PasswordReset', RESET_VALID_MS, 'reset-password', (to, link, expires) => ({
      subject: `${app.name}: reset your password`,
      text: emailBody(to, `Someone asked to reset the password for your ${app.name} account. To choose a new password, follow this link:`, [
        link,
        '',
        `This link works once and expires on ${expires}.`,
        'If you did not ask for this, ignore this email; your password stays the same.',
      ]),
    }));
  };

  const passwordChanged: EventHandler = async ({ event, sendEmail }) => {
    const data = event.data as { userId: string; method: 'Changed' | 'Reset' };
    const how = data.method === 'Changed' ? 'while you were logged in.' : 'through a password recovery link.';
    await sendEmail(data.userId, 'PasswordChanged', (to) => ({
      subject: `${app.name}: your password was changed`,
      text: emailBody(to, `Your ${app.name} password was changed on ${formatInstant(event.occurredAt, to.timeZone)}, ${how}`, [
        `If this was not you, reset your password at ${app.baseUrl}/forgot-password`,
      ]),
    }));
  };

  return new Map<string, EventHandler>([
    ['AccessGranted.v1', accessGranted],
    ['PasswordResetRequested.v1', passwordResetRequested],
    ['PasswordChanged.v1', passwordChanged],
  ]);
}
