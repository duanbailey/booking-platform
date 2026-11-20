# Story: Admin creates an owner and their business
Scenario: Given an admin is logged in
And on the owner creation screen
When they enter the owner's name and email, and the business's name and time zone
And no account exists with that email
Then an account is created for the owner, without a password
And a business is created with that account as its owner and default settings
And an invite email is sent to the owner to set their password.

Scenario: Given an admin is logged in
When they create an owner with an email that belongs to an account that does not own a business
Then a business is created with that account as its owner and default settings
And the owner is emailed that they now own the business.

Scenario: Given an admin is logged in
When they create an owner with an email that belongs to an account that has no password yet and does not own a business
Then a business is created with that account as its owner and default settings
And a new invite email is sent to the owner to set their password, and only the newest link works.

Scenario: Given an admin is logged in
When they create an owner with an email that belongs to an account that already owns a business
Then the creation is refused, and they are told that account already owns a business
And nothing changes.

# Story: Invited user sets their password
Scenario: Given a user has received an invite email
When they follow its link within 7 days and set a password
Then their password is saved, the link stops working, and they are logged in.

Scenario: Given a user has received an invite email
When they follow its link after 7 days, or after it has already been used
Then they are told the link is no longer valid, and nothing changes.

Scenario: Given an account was created with an invite and has no password yet
When an admin (for an owner) or the business's owner (for a staff member) resends the invite
Then a new invite email is sent, and only the newest link works.

Scenario: Given an account already has a password
When someone tries to resend its invite
Then the resend is refused, and nothing changes.

# Story: Owner can log in
Scenario: Given the owner is on the login page.
When they enter valid credentials
Then they successfully access the owner account page.

# Story: Staff can log in
Scenario: Given the staff is on the login page.
When they enter valid credentials
Then they successfully access the staff account page.

# Story: Customer can log in
Scenario: Given the customer is on the login page.
When they enter valid credentials
Then they successfully access the customer account page.

# Story: Customer can sign up
Scenario: Given a visitor is on the sign-up page
When they enter their name, email, password and time zone
And no account exists with that email
Then their account is created, and they access the customer account page.

Scenario: Given a visitor is on the sign-up page
When they enter an email that already belongs to an account
Then the sign-up is refused, and they are told an account with that email already exists.

# Story: User can recover a forgotten password
Scenario: Given a visitor is on the login page
When they request password recovery and enter an email that belongs to an account
Then a recovery email with a link is sent to that address
And they are told that if an account exists for that email, a link has been sent.

Scenario: Given a visitor is on the login page
When they request password recovery and enter an email that does not belong to an account
Then no email is sent
And they are told the same as when the account exists, so the page never reveals which emails have accounts.

Scenario: Given a recovery email was sent for an account less than 5 minutes ago
When recovery is requested for that account again
Then no email is sent, and the visitor is told the same as before.

Scenario: Given a user has received a recovery email
When they follow its link within 1 hour and set a new password
Then their password is saved, the link stops working, they are logged out everywhere else, and they are logged in
And they are emailed that their password was changed.

Scenario: Given a user has received a recovery email
When they follow its link after 1 hour, after it has been used, or after a newer recovery email was sent
Then they are told the link is no longer valid, and nothing changes.

Scenario: Given an account was created with an invite and has no password yet
When recovery is requested for it and the user sets a password through the link
Then the password is saved as if the invite had been accepted, and the invite links stop working
And no password-changed email is sent, since the account had no password before (decided 2026-11-19).

# Story: User can change their password
Scenario: Given a user is logged in and chooses to change their password
When they enter their current password and a new password
Then their password is updated, they are logged out everywhere else, and they stay logged in
And they are emailed that their password was changed.

Scenario: Given a user is logged in and chooses to change their password
When they enter a wrong current password
Then they are told the current password is incorrect, and nothing changes. An empty current password counts as wrong. The new password may equal the current one (decided 2026-11-19).

# Story: Owner sets up their business
Scenario (R2, R3, R4, R8): Given the owner is logged in
And on the owner account page
When they change the business's name or settings (minimum notice, cancellation window, advance-booking window, slot step)
Then the changes are saved, and settings they have not changed keep their defaults.

# Story: Owner adds staff
Scenario: Given the owner is logged in
And on the owner account page
When they add a staff member by name and email
And no account exists with that email
Then an account is created without a password, it is added as staff in the business, and an invite email is sent to set their password.

Scenario: Given the owner is logged in
When they add a staff member with an email that belongs to an account
Then that account is added as staff in the business, and is emailed that they have been added.

Scenario: Given the owner is logged in
When they add themselves as staff
Then their account is added as staff in the business, so they can take bookings.

Scenario: Given the owner is logged in
When they add a staff member with an email whose account is already staff in the business
Then the addition is refused, and nothing changes.

# Story: Owner removes staff
Scenario: Given the owner is logged in
And a staff member has no bookings that have not started
When the owner removes the staff member
Then the staff member is removed from the business, can no longer act as staff in it, and slot generation no longer offers their slots.

Scenario (R6): Given the owner is logged in
And a staff member has bookings that have not started
When the owner removes the staff member
Then display a warning message listing those bookings to confirm
And if confirmed, remove the staff member, cancel those bookings with the reason StaffRemoved, and send cancellation notifications to all affected users
And if not confirmed, nothing changes.

Scenario: Given a staff member was removed from a business
When the owner adds them again
Then they are staff in the business again, with their earlier bookings still linked to them.

# Story: Owner assigns staff to services
Scenario: Given a staff member and one or more services exist in the owner's business
When the owner selects the staff member and the services they perform
Then the staff member's services are replaced by the selected ones
And slot viewing for each selected service includes that staff member.

# Story: Owner defines services (name, optional description, duration, buffer before/after, price).
Scenario: Given the owner is logged in
And the owner is on the service creation form
When the owner enters valid service creation details
Then the service is created in their business.

# Story: Staff member has weekly recurring availability plus date exceptions.
Scenario (R8): Given the staff member is logged in
And the staff member is on the staff account page
When the staff member specifies their weekly availability
Then the availability is saved, and slot generation offers slots within it. An exception for a date takes precedence and replaces the availability for that date, either modifying the hours or marking the day as unavailable.

Scenario (R8): Given the staff member is logged in
And the staff member is on the staff account page
When the staff member sets different hours for a specific date
Then the exception is saved, and for that date slot generation offers slots only within the new hours, not the weekly availability.

Scenario (R8): Given the staff member is logged in
And the staff member is on the staff account page
When the staff member marks a specific date as unavailable
Then the exception is saved, and slot generation offers no slots for them on that date.

Scenario (R6, R8): Given the staff member or owner sets a date exception
When the exception leaves existing customer bookings outside the staff member's availability for that date
Then display a warning message listing those bookings to confirm
And if confirmed, save the exception, cancel those bookings with the reason AvailabilityChanged, and send cancellation notifications to all affected users
And if not confirmed, nothing changes.

Scenario (R6, R8): Given the staff member or owner changes the staff member's weekly availability
When the change leaves existing customer bookings outside the staff member's availability
Then display a warning message listing those bookings to confirm
And if confirmed, save the change, cancel those bookings with the reason AvailabilityChanged, and send cancellation notifications to all affected users
And if not confirmed, nothing changes.

# Story: Customer views open slots for a service and date range.
Scenario (R2, R8): Given the customer is on the page for a specific service
When they select a date range
Then they view the open slots for all staff assigned to that service in that range.

Scenario (R8): Given the customer is on the page for a specific service
When they select a date range longer than 30 days
Then no slots are returned
And the customer is told the maximum range is 30 days.

# Story: Customer books a slot and gets a confirmation.
Scenario (R1, R3): Given the customer is viewing an open slot
And they are not within the business's minimum notice period
When they select to book the available slot
Then the slot is booked, and a confirmation email is sent to the customer.

Scenario (R1, R7): Given the customer is viewing an open slot
When they select to book the slot
And the slot's time, including buffers, has already been booked for that staff member
Then the booking is refused
And the customer is notified of the conflict, with the requested slot's staff member, start time and end time (R7).

Scenario (R3): Given the customer is viewing a slot
When they select to book the slot
And the slot now starts within the business's minimum notice period
Then the booking is refused
And the customer is told the slot is too soon to book, and shown the minimum notice period.

# Story: Staff or owner books on behalf of a customer.
Scenario (R1, R3, R8): Given the owner is logged in
And an account exists with the customer's email
When the owner books an open slot with any staff member in their business, entering the customer's email
Then the booking is made with that account as its customer, and a confirmation email is sent to the customer.

Scenario (R1, R3, R8): Given a staff member is logged in
And an account exists with the customer's email
When the staff member books one of their own open slots, entering the customer's email
Then the booking is made with that account as its customer, and a confirmation email is sent to the customer.

Scenario: Given the owner or a staff member is booking on behalf of a customer
When they enter an email that does not belong to any account
Then the booking is refused, and they are told no account exists with that email
And nothing changes.

Scenario: Given a staff member is logged in
When they try to book a customer with another staff member
Then the booking is refused, and nothing changes.

Scenario (R4): Given the owner or a staff member is booking on behalf of a customer
And the customer holds a booking with the business that starts in less than the business's cancellation window (N hours)
Then before they confirm, they are warned that the customer's original booking still stands
And if they confirm, the new booking is made, the original booking is kept, and the original booking's staff member and the business's owner are notified that the customer holds both bookings.

Scenario: Given the owner or a staff member books on behalf of a customer
When the booking is made
Then the booking's history records the staff member or owner as the person who made it.

# Story: Customer cancels or reschedules, subject to the cancellation window.
Scenario (R4, R8): Given the customer has selected a booked slot
And the booking starts N or more hours from now (outside the business's cancellation window)
When they choose to cancel
Then display a warning message to confirm
And if confirmed, cancel the booking, so slot generation offers its time again, and send cancellation notifications to all affected users.

Scenario (R3, R4, R5, R8): Given the customer has selected a booked slot
And the booking starts N or more hours from now (outside the business's cancellation window)
When they choose to reschedule to a new valid slot with the same staff member
Then display a warning message to confirm
And if confirmed, move the booking to the new slot, so slot generation offers its original time again, and send a rescheduling notification to all affected users.

Scenario (R4): Given the customer has selected a booked slot
And the booking starts in less than the business's cancellation window (N hours)
When they choose to cancel
Then the cancellation is refused
And the customer is told they can no longer cancel this booking.

Scenario (R4): Given the customer has selected a booked slot
And the booking starts in less than the business's cancellation window (N hours)
When they choose to reschedule
Then the reschedule is refused
And the customer is told they can no longer reschedule this booking, and must book a new slot instead.

Scenario (R4): Given the customer holds a booking with a business that starts in less than the business's cancellation window (N hours)
When they choose to book another slot with the same business
Then before they confirm, they are warned that the original booking still stands and that they must contact the business to cancel it
And if they confirm, the new booking is made, the original booking is kept, and the original booking's staff member and the business's owner are notified that the customer holds both bookings.

# Story: Staff or owner blocks time off, which handles existing bookings.
Scenario (R6): Given the staff or owner blocks time off
When the time off overlaps existing customer bookings
Then display a warning message listing the overlapping bookings to confirm
And if confirmed, save the time off, cancel the overlapping bookings, and send cancellation notifications to all affected users
And slot generation no longer offers slots during the time off.

# Story: Reminders go out 24 hours and 1 hour before a booking.
Scenario (R9): Given a valid booking
When it is 24 hours or 1 hour before a booking
Then a reminder is emailed to the customer.

Scenario (R9): Given a booking is made less than 24 hours before it starts
When the 24-hour reminder time has already passed
Then the 24-hour reminder is skipped and not sent
And the 1-hour reminder is still sent if its time has not passed.

Scenario (R6, R9): Given a booking has pending reminders
When the booking is cancelled, by the customer, staff, owner, or time off
Then its pending reminders are deleted and never sent.

# Story: Owner or staff overrides the minimum notice period for an individual booking.
Scenario (R1, R3, R8): Given the owner is logged in
And a slot for a staff member in their business starts within the minimum notice period
When the owner books or reschedules into that slot with the notice override
Then the booking is made
And the override is recorded in the booking's history with the owner as the person who made it.

Scenario (R1, R3, R8): Given a staff member is logged in
And a slot for that staff member starts within the minimum notice period
When the staff member books or reschedules their own booking into that slot with the notice override
Then the booking is made
And the override is recorded in the booking's history with the staff member as the person who made it.

Scenario (R3): Given a staff member is logged in
When they try to override the minimum notice period for a booking with another staff member
Then the override is refused, and nothing changes.

Scenario (R1, R3): Given the owner or a staff member uses the notice override
When the slot starts in the past, or its time, including buffers, is already booked for that staff member
Then the booking is refused, because the override bypasses only the minimum notice period.

# Story: Owner changes the business's time zone.
Scenario (R2): Given the owner is logged in
And no bookings that have not started fall outside availability in the new time zone
When the owner changes the business's time zone
Then the time zone is changed, and existing bookings keep their UTC times
And customers whose bookings now fall at a different local time in the business's time zone are notified of the new local times
And the change is recorded with the old and new time zones, the owner, and the time of the change.

Scenario (R2, R6): Given the owner is logged in
When the owner changes the business's time zone
And bookings that have not started fall outside availability in the new time zone
Then display a warning message listing those bookings to confirm
And if confirmed, change the time zone, cancel those bookings with the reason AvailabilityChanged, send cancellation notifications to all affected users, notify the customers of the remaining bookings whose local times changed, and record the change
And if not confirmed, nothing changes.