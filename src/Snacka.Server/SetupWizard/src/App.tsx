import { useState, useEffect } from 'react'
import './App.css'

interface ServerInfo {
  serverName: string
  hasUsers: boolean
  bootstrapInviteCode: string | null
}

interface SetupData {
  serverName: string
  adminUsername: string
  adminEmail: string
  adminPassword: string
  confirmPassword: string
}

type Step = 'loading' | 'already-setup' | 'welcome' | 'server-name' | 'admin-account' | 'complete'

function App() {
  const [step, setStep] = useState<Step>('loading')
  const [serverInfo, setServerInfo] = useState<ServerInfo | null>(null)
  const [setupData, setSetupData] = useState<SetupData>({
    serverName: 'My Snacka Server',
    adminUsername: '',
    adminEmail: '',
    adminPassword: '',
    confirmPassword: '',
  })
  const [error, setError] = useState<string | null>(null)
  const [isSubmitting, setIsSubmitting] = useState(false)

  useEffect(() => {
    checkServerStatus()
  }, [])

  const checkServerStatus = async () => {
    try {
      const response = await fetch('/api/auth/server-info')
      if (response.ok) {
        const info: ServerInfo = await response.json()
        setServerInfo(info)
        if (info.hasUsers) {
          setStep('already-setup')
        } else {
          setStep('welcome')
        }
      } else {
        setError('Failed to connect to server')
      }
    } catch {
      setError('Failed to connect to server. Is the backend running?')
    }
  }

  const handleSubmit = async () => {
    setError(null)

    if (setupData.adminPassword !== setupData.confirmPassword) {
      setError('Passwords do not match')
      return
    }

    if (setupData.adminPassword.length < 8) {
      setError('Password must be at least 8 characters')
      return
    }

    if (!setupData.adminUsername || setupData.adminUsername.length < 3) {
      setError('Username must be at least 3 characters')
      return
    }

    if (!setupData.adminEmail || !setupData.adminEmail.includes('@')) {
      setError('Please enter a valid email address')
      return
    }

    setIsSubmitting(true)

    try {
      // First, update server name if needed
      // For now, we'll just create the admin account
      // Server name can be added to a setup endpoint later

      const response = await fetch('/api/auth/register', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          username: setupData.adminUsername,
          email: setupData.adminEmail,
          password: setupData.adminPassword,
          inviteCode: serverInfo?.bootstrapInviteCode,
        }),
      })

      if (response.ok) {
        setStep('complete')
      } else {
        const errorData = await response.json()
        setError(errorData.error || 'Registration failed')
      }
    } catch {
      setError('Failed to create account. Please try again.')
    } finally {
      setIsSubmitting(false)
    }
  }

  const renderStep = () => {
    switch (step) {
      case 'loading':
        return (
          <div className="setup-card">
            <div className="spinner"></div>
            <p>Connecting to server...</p>
          </div>
        )

      case 'already-setup':
        return (
          <div className="setup-card">
            <div className="icon success-icon">✓</div>
            <h1>Server Already Set Up</h1>
            <p className="description">
              This server has already been configured. Use the Snacka desktop app to connect.
            </p>
            <a href="/" className="button secondary">Go to Home</a>
          </div>
        )

      case 'welcome':
        return (
          <div className="setup-card">
            <div className="logo">
              <svg viewBox="0 0 24 24" width="64" height="64" fill="currentColor">
                <path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z"/>
              </svg>
            </div>
            <h1>Welcome to Snacka</h1>
            <p className="description">
              Let's set up your server. This will only take a minute.
            </p>
            <button className="button primary" onClick={() => setStep('server-name')}>
              Get Started
            </button>
          </div>
        )

      case 'server-name':
        return (
          <div className="setup-card">
            <h1>Name Your Server</h1>
            <p className="description">
              Choose a name that your community will recognize.
            </p>
            <div className="form-group">
              <label htmlFor="serverName">Server Name</label>
              <input
                id="serverName"
                type="text"
                value={setupData.serverName}
                onChange={(e) => setSetupData({ ...setupData, serverName: e.target.value })}
                placeholder="My Snacka Server"
              />
            </div>
            <div className="button-group">
              <button className="button secondary" onClick={() => setStep('welcome')}>
                Back
              </button>
              <button 
                className="button primary" 
                onClick={() => setStep('admin-account')}
                disabled={!setupData.serverName.trim()}
              >
                Continue
              </button>
            </div>
          </div>
        )

      case 'admin-account':
        return (
          <div className="setup-card">
            <h1>Create Admin Account</h1>
            <p className="description">
              This will be the server administrator account with full control.
            </p>
            {error && <div className="error-message">{error}</div>}
            <div className="form-group">
              <label htmlFor="username">Username</label>
              <input
                id="username"
                type="text"
                value={setupData.adminUsername}
                onChange={(e) => setSetupData({ ...setupData, adminUsername: e.target.value })}
                placeholder="admin"
                autoComplete="username"
              />
            </div>
            <div className="form-group">
              <label htmlFor="email">Email</label>
              <input
                id="email"
                type="email"
                value={setupData.adminEmail}
                onChange={(e) => setSetupData({ ...setupData, adminEmail: e.target.value })}
                placeholder="admin@example.com"
                autoComplete="email"
              />
            </div>
            <div className="form-group">
              <label htmlFor="password">Password</label>
              <input
                id="password"
                type="password"
                value={setupData.adminPassword}
                onChange={(e) => setSetupData({ ...setupData, adminPassword: e.target.value })}
                placeholder="At least 8 characters"
                autoComplete="new-password"
              />
            </div>
            <div className="form-group">
              <label htmlFor="confirmPassword">Confirm Password</label>
              <input
                id="confirmPassword"
                type="password"
                value={setupData.confirmPassword}
                onChange={(e) => setSetupData({ ...setupData, confirmPassword: e.target.value })}
                placeholder="Confirm your password"
                autoComplete="new-password"
              />
            </div>
            <div className="button-group">
              <button className="button secondary" onClick={() => setStep('server-name')}>
                Back
              </button>
              <button 
                className="button primary" 
                onClick={handleSubmit}
                disabled={isSubmitting}
              >
                {isSubmitting ? 'Creating...' : 'Create Account'}
              </button>
            </div>
          </div>
        )

      case 'complete':
        return (
          <div className="setup-card">
            <div className="icon success-icon">✓</div>
            <h1>Setup Complete!</h1>
            <p className="description">
              Your server is ready. You can now connect using the Snacka desktop app.
            </p>
            <div className="info-box">
              <p><strong>Server:</strong> {setupData.serverName}</p>
              <p><strong>Admin:</strong> {setupData.adminUsername}</p>
            </div>
            <p className="hint">
              To invite others, create invite codes from the Admin Panel in the desktop app.
            </p>
          </div>
        )
    }
  }

  return (
    <div className="setup-container">
      {renderStep()}
      <div className="step-indicator">
        {step !== 'loading' && step !== 'already-setup' && (
          <>
            <div className={`step ${['welcome', 'server-name', 'admin-account', 'complete'].includes(step) ? 'active' : ''}`} />
            <div className={`step ${['server-name', 'admin-account', 'complete'].includes(step) ? 'active' : ''}`} />
            <div className={`step ${['admin-account', 'complete'].includes(step) ? 'active' : ''}`} />
            <div className={`step ${step === 'complete' ? 'active' : ''}`} />
          </>
        )}
      </div>
    </div>
  )
}

export default App
