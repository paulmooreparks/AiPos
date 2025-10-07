# AiPos
AI-Driven Point of Sale System

## Project Overview

AiPos is a culture-neutral, AI-driven Point of Sale system that uses AI orchestration to handle customer interactions and transaction processing. The system provides identical functionality through three distinct access patterns while maintaining cultural adaptability through store-specific extensions.

## Architecture

The system is built on a three-layer architecture that separates concerns between AI intelligence, store-specific business logic, and the core transaction kernel:

- **AiPos Layer**: AI orchestration, agentic interfaces, and intelligent customer interaction
- **PosKernel Layer**: Culture-neutral transaction processing and store extension framework
- **Demo Layer**: Reference implementations showing how to build store-specific solutions

## Key Principles

- **Culture-Neutral Core**: The kernel knows nothing about currencies, languages, or business rules
- **Fail-Fast Design**: Missing configuration causes immediate failure with clear error messages
- **AI Orchestration**: Single-call pattern for predictable performance and simplified debugging
- **Store Extensions**: All cultural and business logic handled by pluggable extensions

## Current Status

This project represents a new architectural approach focused on AI orchestration rather than inference loops. The design emphasizes:

- Clean separation between AI intelligence and transaction processing
- Demonstrable implementations for food service verticals
- Security-first agentic design with proper privilege boundaries
- Culture-neutral interfaces that support global deployment

## Documentation

- **[ARCHITECTURE.md](ARCHITECTURE.md)** - Complete technical architecture and design decisions
- **[PRESENTATION.md](PRESENTATION.md)** - Executive overview for business stakeholders

## Directory Structure

```
AiPos/          # Pure AI/agent architecture
PosKernel/      # Pure kernel architecture
Demo/           # Implementation examples
```

The Demo directory contains reference implementations including terminal UI and food service store examples (Singapore Kopitiam and American Coffee Shop patterns) that demonstrate how to implement the architecture for specific business contexts.

## Development Phase

Currently in architectural design and proof-of-concept development. The focus is on establishing clean patterns for AI-driven POS systems that can be adapted to different cultural and business contexts without compromising the core transaction integrity.
