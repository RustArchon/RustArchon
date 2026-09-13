## Setting up Admin Access

To set up admin access in development mode:

1. Ensure `RUSTARCHON_ADMIN_EMAIL` and `RUSTARCHON_ADMIN_CODE` are set correctly in your `.env` file
2. Run RustArchon.Api to seed the bootstrap invitation code
3. Register a new account through the Panel UI using:
   - Email: `cyberknet@gmail.com` 
   - Code: `DEV-BOOTSTRAP-CODE`
4. The registered user will automatically be granted platform admin privileges
5. Once registered, you can enable SingleUserAuth in `appsettings.Development.json` to automatically log in as that user

The single-user authentication mode requires the account to already exist in the identity database (RustArchon_Identity).