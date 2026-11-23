import { SendEmailCommand, type SendEmailCommandOutput } from '@aws-sdk/client-sesv2';

export interface EmailContent {
  subject: string;
  text: string;
  html?: string;
}

export interface OutgoingEmail extends EmailContent {
  to: string;
}

/** Sends one email and returns the provider's message ID (ADR 0007: SES in production, a fake in tests). */
export interface EmailSender {
  send(email: OutgoingEmail): Promise<string>;
}

/** The part of SESv2Client this adapter uses, so tests can pass a stand-in. */
export interface SesClientLike {
  send(command: SendEmailCommand): Promise<Partial<SendEmailCommandOutput>>;
}

/** SES (API v2) SendEmail with a simple message: UTF-8 subject, text body and optional HTML body. */
export class SesEmailSender implements EmailSender {
  constructor(private readonly client: SesClientLike, private readonly from: string) {}

  async send(email: OutgoingEmail): Promise<string> {
    const output = await this.client.send(new SendEmailCommand({
      FromEmailAddress: this.from,
      Destination: { ToAddresses: [email.to] },
      Content: {
        Simple: {
          Subject: { Data: email.subject, Charset: 'UTF-8' },
          Body: {
            Text: { Data: email.text, Charset: 'UTF-8' },
            ...(email.html === undefined ? {} : { Html: { Data: email.html, Charset: 'UTF-8' } }),
          },
        },
      },
    }));
    if (!output.MessageId) throw new Error('SES returned no MessageId.');
    return output.MessageId;
  }
}
