# Git Workflow Rules

## Branch Protection

### NEVER Push Directly to Main/Master
- **ALWAYS** create a feature branch for any changes
- **ALWAYS** use pull requests to merge changes into main/master
- **NEVER** use `git push origin main` or `git push origin master`
- **NEVER** bypass branch protection rules

### Proper Workflow

1. **Create a feature branch**:
   ```bash
   git checkout -b feature/description
   # or
   git checkout -b fix/description
   ```

2. **Make changes and commit**:
   ```bash
   git add .
   git commit -m "description"
   ```

3. **Push to feature branch**:
   ```bash
   git push origin feature/description
   ```

4. **Create Pull Request**:
   ```bash
   gh pr create --title "..." --body "..."
   ```

### Branch Naming Conventions
- Features: `feature/short-description`
- Bug fixes: `fix/short-description`
- Chores: `chore/short-description`
- Documentation: `docs/short-description`

### Pull Request Requirements
- Include comprehensive description
- Reference related issues
- Ensure all tests pass
- Wait for CI/CD checks
- Request review when needed

### Exception Handling
If you accidentally start work on main:
```bash
# Create a new branch from current state
git checkout -b feature/description

# Reset main to remote state
git checkout main
git reset --hard origin/main

# Switch back to feature branch
git checkout feature/description
```

## Rationale
Pushing directly to main/master:
- Bypasses code review
- Bypasses CI/CD checks
- Can break the build for other developers
- Violates team workflow standards
- May violate repository branch protection rules
