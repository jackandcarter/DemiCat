# Manual Test: Multiple Officer Roles

1. Run the `/setup` or `/settings` command to open the configuration wizard.
2. When prompted for officer roles, select multiple roles from the multi-select and finish the wizard.
   * Verify that the summary lists all selected officer roles.
3. Assign one of the chosen officer roles to a user and execute `/key`.
   * The command should succeed and the role should be marked as an officer role in the database.
