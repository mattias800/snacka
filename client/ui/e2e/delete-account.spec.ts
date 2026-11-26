import { SERVER, ensureUser, expect, newPage, PASSWORD, test } from "./support";

/** A user deletes their own account from Settings: wrong password first, then for real. */

test("Deleting your account", async ({ browser }) => {
  const name = `gone${Date.now().toString(36)}`;
  await ensureUser(name);
  const page = await newPage(browser);
  await page.goto("/");
  await page.getByLabel("Server").fill(SERVER);
  await page.getByLabel("Email").fill(`${name}@test.com`);
  await page.getByLabel("Password").fill(PASSWORD);
  await page.getByRole("button", { name: "Log in" }).click();

  await page.getByTestId("open-settings").click();
  await page.getByTestId("delete-account").click();
  page.on("dialog", (d) => void d.accept());
  await page.getByLabel("Password to delete account").fill("Wrong123!");
  await page.getByRole("button", { name: "Delete account" }).click();
  await expect(page.getByTestId("delete-account-error")).toContainText("incorrect");

  await page.getByLabel("Password to delete account").fill(PASSWORD);
  await page.getByRole("button", { name: "Delete account" }).click();
  await expect(page.getByRole("button", { name: "Log in" })).toBeVisible();

  const login = await fetch(`${SERVER}/api/Auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email: `${name}@test.com`, password: PASSWORD }) });
  expect(login.status).toBe(401);
});
