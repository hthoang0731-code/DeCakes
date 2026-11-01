-- Backend account for the second developer: decakes_app_member2@%.
-- Run with the Aiven administrator connection, after creating this NEW account.
-- Before running, SHOW GRANTS must show only USAGE and no role assignments.
-- This script ADDS privileges; it does not remove any existing privileges.
-- Never store a password in this file.
-- Same privileges as app_user_grants.sql; no changes to tables or business data.

GRANT SELECT ON decakes_db.* TO 'decakes_app_member2'@'%';
GRANT EXECUTE ON decakes_db.* TO 'decakes_app_member2'@'%';

GRANT INSERT, UPDATE ON decakes_db.users TO 'decakes_app_member2'@'%';
GRANT INSERT, UPDATE ON decakes_db.bakeries TO 'decakes_app_member2'@'%';
GRANT INSERT, UPDATE ON decakes_db.bakery_members TO 'decakes_app_member2'@'%';
GRANT INSERT, UPDATE ON decakes_db.cakes TO 'decakes_app_member2'@'%';
GRANT INSERT, UPDATE ON decakes_db.design_requests TO 'decakes_app_member2'@'%';
GRANT INSERT ON decakes_db.media_assets TO 'decakes_app_member2'@'%';

GRANT UPDATE (status, decline_reason, responded_at, updated_at)
  ON decakes_db.request_bakeries TO 'decakes_app_member2'@'%';

GRANT UPDATE (status, picked_up_at, cancelled_at, updated_at)
  ON decakes_db.orders TO 'decakes_app_member2'@'%';

SHOW GRANTS FOR 'decakes_app_member2'@'%';
