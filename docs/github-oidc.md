# GitHub Actions → AWS with OIDC

`deploy.yml` uses short-lived credentials from GitHub's OIDC provider, so there are no AWS access keys
stored in GitHub. Do this once per AWS account.

## 1. Add GitHub as an identity provider

IAM console → **Identity providers** → **Add provider**:

- Type: OpenID Connect
- Provider URL: `https://token.actions.githubusercontent.com`
- Audience: `sts.amazonaws.com`

## 2. Create the deploy role

Create a role with this **trust policy**. Replace `ACCOUNT_ID`, `OWNER` and `REPO`:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Federated": "arn:aws:iam::ACCOUNT_ID:oidc-provider/token.actions.githubusercontent.com" },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": { "token.actions.githubusercontent.com:aud": "sts.amazonaws.com" },
      "StringLike": { "token.actions.githubusercontent.com:sub": "repo:OWNER/REPO:*" }
    }
  }]
}
```

For **permissions**, the role only needs to assume the roles that `cdk bootstrap` created:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": "sts:AssumeRole",
    "Resource": "arn:aws:iam::ACCOUNT_ID:role/cdk-*"
  }]
}
```

## 3. Configure the repository

- Secret `AWS_DEPLOY_ROLE_ARN`: the role's ARN
- Variables (optional): `AWS_REGION`, and `OPS_EMAIL` (receives ops pages and platform alarms)
- Environments `dev` and `prod`: add required reviewers on `prod` for a manual approval gate

## 4. Deploy

Deploys are manual: **Actions → Deploy → Run workflow**, then pick `dev` or `prod`. Pushing to `main` only runs CI (build, tests, `cdk synth`), so the repo stays green before AWS is set up.
