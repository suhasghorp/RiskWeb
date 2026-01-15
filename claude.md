## Project specifications

- **Project Name:** RiskWeb
- **Description:** An intranet web application for risk assessment and management.
- **Technologies Used:** asp.net Blazor, MudBlazor Blazor components, Entity Framework Core, SQL Server.

- **Implementation Plan:**
- 1. Review riskweb-excalidraw.png file to understand the general screen layout and user interface.
	1. Set up the Blazor project structure and integrate MudBlazor components.
- 2. The screen is divided into header, sidebar, and main content area.
	1. Implement the header using the images/ABLogoHorizRev.png file as the logo.
	2. The headertext "Risk Web" is the application name displayed prominently in the header.
	3. The "Liq", "SEC" and "Lux" are links in the header for navigating to different modules.
	4. Create a collapsible sidebar for navigation links to different sections of the application. 
	   - These links will be different based on the selection made in the header.
		- Generate dummy links for each of the header link options for now; actual links will be defined later.
	5. The header also displays the currently captured login id of the user on the top right corner. This application will be hosted on IIS Express on Windows server.
	6. The content area will display different components based on the navigation selection.
- 3. Colors and styling
	1. Use Blue (#1E9BD7) as the primary color for the header background. The text should be White. The sidebar background should be White with Balck text. 
      Once selected, the sidebar link background should change to Blue and remain selected until another link is clicked. 
	  When the sidebar is collapsed and re-opened, the selection should persist.
  